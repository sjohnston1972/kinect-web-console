# Self-test: builds its own copy of the bridge, runs it with the fake (mock) sensor on a spare port with a
# throwaway data folder, and checks every feature through the web page's own connection and a headless browser.
# It never touches the real app, settings.json or captures, so it can run while the app is open.
#
# Usage: double-click selftest.cmd, or: powershell -File tools\selftest.ps1 [-Port 8799] [-Keep]
# Prints PASS or FAIL for each check and exits with 0 when everything passed.
param([int]$Port = 8799, [switch]$Keep)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path)
$work = Join-Path $env:TEMP ("kinect-selftest-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$origin = "http://localhost:$Port"
$results = New-Object System.Collections.Generic.List[object]
$bridge = $null
$edge = $null

function Report($name, $ok, $detail) {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0}  {1}" -f $mark, $name) -ForegroundColor $colour -NoNewline
    if ($detail) { Write-Host "  ($detail)" -ForegroundColor DarkGray } else { Write-Host "" }
}

# Runs one check. The script block returns $true/$false, or a string explaining a failure.
function Check($name, [scriptblock]$test) {
    try {
        $result = & $test
        if ($result -is [string]) { Report $name $false $result }
        elseif ($result -is [array]) { Report $name ([bool]$result[0]) ([string]$result[1]) }
        else { Report $name ([bool]$result) $null }
    } catch {
        Report $name $false $_.Exception.Message
    }
}

# ----- Plain HTTP, without throwing on 4xx and 5xx -----
function HttpStatus($url, $headers = @{}) {
    $request = [Net.HttpWebRequest]::Create($url)
    $request.Timeout = 5000
    foreach ($k in $headers.Keys) { if ($k -eq 'Host') { $request.Host = $headers[$k] } else { $request.Headers[$k] = $headers[$k] } }
    try { $response = $request.GetResponse() } catch [Net.WebException] { $response = $_.Exception.Response }
    if ($null -eq $response) { return 0 }
    $reader = New-Object IO.StreamReader($response.GetResponseStream())
    $body = $reader.ReadToEnd(); $reader.Close()
    return @{ Status = [int]$response.StatusCode; Body = $body; Length = $response.ContentLength }
}

function DownloadBytes($url) {
    $client = New-Object Net.WebClient
    return $client.DownloadData($url)
}

# ----- A WebSocket client like the page's -----
function New-Client($originHeader = $origin) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    if ($originHeader) { $ws.Options.SetRequestHeader("Origin", $originHeader) }
    $ws.ConnectAsync([Uri]"ws://localhost:$Port/ws", [Threading.CancellationToken]::None).Wait()
    return @{ Ws = $ws; Pending = $null; Buffer = (New-Object byte[] (4MB)) }
}

function Send($client, $json) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $client.Ws.SendAsync((New-Object ArraySegment[byte] -ArgumentList (, $bytes)), 'Text', $true, [Threading.CancellationToken]::None).Wait()
}

# The next message as @{ Binary; Type; Data; Text }, or $null after the timeout
function Receive($client, $timeoutMs) {
    $ms = New-Object IO.MemoryStream
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ($true) {
        if (-not $client.Pending) {
            $client.Pending = $client.Ws.ReceiveAsync((New-Object ArraySegment[byte] -ArgumentList (, $client.Buffer)), [Threading.CancellationToken]::None)
        }
        $left = [int][Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds)
        if (-not $client.Pending.Wait($left)) { return $null }
        $r = $client.Pending.Result; $client.Pending = $null
        $ms.Write($client.Buffer, 0, $r.Count)
        if ($r.EndOfMessage) { break }
    }
    $data = $ms.ToArray()
    if ($r.MessageType -eq 'Binary') { return @{ Binary = $true; Type = [int]$data[0]; Data = $data } }
    return @{ Binary = $false; Text = [Text.Encoding]::UTF8.GetString($data) }
}

# Collects messages for a while; returns them all
function Collect($client, $seconds) {
    $list = New-Object System.Collections.Generic.List[object]
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        $m = Receive $client ([int][Math]::Max(1, ($end - (Get-Date)).TotalMilliseconds))
        if ($m) { $list.Add($m) }
    }
    return $list
}

# Waits for a JSON message matching a pattern; returns it parsed, or $null
function WaitFor($client, $pattern, $seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        $m = Receive $client ([int][Math]::Max(1, ($end - (Get-Date)).TotalMilliseconds))
        if ($m -and -not $m.Binary -and $m.Text -match $pattern) { return ($m.Text | ConvertFrom-Json) }
    }
    return $null
}

function Close($client) { try { $client.Ws.Abort() } catch { } }

try {
    Write-Host ""
    Write-Host "Kinect Web Console self-test" -ForegroundColor Cyan
    Write-Host "  Working folder: $work"
    New-Item -ItemType Directory -Force $work | Out-Null

    # ----- Build a private copy, so the running app (and its locked files) are left alone -----
    $dotnet = if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { "$env:ProgramFiles\dotnet\dotnet.exe" } else { "dotnet" }
    $buildLog = & $dotnet build (Join-Path $root "bridge\KinectBridge.csproj") -c Release -nologo -v q "-p:OutputPath=$work\bin\" 2>&1
    $built = $LASTEXITCODE -eq 0
    Check "The bridge builds" { if ($built) { $true } else { ($buildLog | Select-String "error" | Select-Object -First 1).Line } }
    if (-not $built) { throw "Stopping: the build failed" }

    # Starts the test bridge and waits until it answers. Returns true if it did.
    function Start-Bridge($logName) {
        $script:bridge = Start-Process "$work\bin\KinectBridge.exe" -ArgumentList "--mock", "--no-browser", "--port", $Port, "--data-folder", "$work\data" `
            -WorkingDirectory $root -PassThru -WindowStyle Hidden -RedirectStandardOutput "$work\$logName"
        for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; try { if ((HttpStatus "$origin/").Status -eq 200) { return $true } } catch { } }
        return $false
    }
    $up = Start-Bridge "bridge-output.txt"
    Check "The bridge starts and serves the page" { if ($up) { $true } else { "nothing answered on port $Port within 10 seconds" } }
    if (-not $up) { throw "Stopping: the bridge did not start" }

    # ----- The web page and local-only rules -----
    Write-Host "Web page and security" -ForegroundColor Cyan
    Check "The page loads with its title" { (HttpStatus "$origin/").Body -match '<title>Kinect Web Console</title>' }
    Check "Scripts and the 3D library load" { (HttpStatus "$origin/js/app.js").Status -eq 200 -and (HttpStatus "$origin/vendor/three/three.module.min.js").Status -eq 200 }
    Check "Files outside the web folder are refused" {
        $codes = @("$origin/..%2F..%2Fsettings.json", "$origin/captures/..%2F..%2Fsettings.json", "$origin/..%5Csettings.json", "$origin/captures/..%5C..%5CSPEC.md") | ForEach-Object { (HttpStatus $_).Status }
        if ($codes | Where-Object { $_ -eq 200 }) { "one was served: $($codes -join ', ')" } else { @($true, "answers: $($codes -join ', ')") }
    }
    Check "A WebSocket from another website is refused" {
        try { $c = New-Client "http://evil.example"; Close $c; "it was accepted" } catch { $true }
    }
    $lan = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.PrefixOrigin -in 'Dhcp', 'Manual' -and $_.IPAddress -notlike '169.*' -and $_.InterfaceAlias -notmatch 'Loopback' } | Select-Object -First 1).IPAddress
    if ($lan) {
        Check "Requests to this PC's network address are refused" {
            $plain = (HttpStatus "http://${lan}:$Port/").Status
            $disguised = (HttpStatus "http://${lan}:$Port/" @{ Host = "localhost:$Port" }).Status
            if ($plain -eq 200 -or $disguised -eq 200) { "served to $lan" } else { @($true, "$lan answered $plain and $disguised") }
        }
    }

    # ----- Status, streams and skeletons -----
    Write-Host "Sensor, streams and skeletons" -ForegroundColor Cyan
    $c = New-Client
    $status = WaitFor $c '"type":"status".*"state":"ready"' 6
    Check "Status arrives: mock sensor ready, amber light" { $status -and $status.mock -and $status.sensor.light -eq 'amber' }

    Send $c '{"type":"subscribe","v":1,"streams":["colour","depth","depthRaw","skeletons"]}'
    $null = Collect $c 0.5
    $messages = Collect $c 3
    $count = @{ 1 = 0; 2 = 0; 3 = 0 }; $skeletonMessages = @(); $jpegOk = $true
    foreach ($m in $messages) {
        if ($m.Binary) { $count[$m.Type]++; if ($m.Type -le 2 -and -not ($m.Data[9] -eq 0xFF -and $m.Data[10] -eq 0xD8)) { $jpegOk = $false } }
        elseif ($m.Text -match '"type":"skeletons"') { $skeletonMessages += $m.Text }
    }
    Check "Colour stream runs at 25+ frames a second" { @(($count[1] -ge 75), "$([math]::Round($count[1] / 3)) per second") }
    Check "Depth stream runs at 25+ frames a second" { @(($count[2] -ge 75), "$([math]::Round($count[2] / 3)) per second") }
    Check "Raw depth (3D points) runs at 25+ frames a second" { @(($count[3] -ge 75), "$([math]::Round($count[3] / 3)) per second") }
    Check "Pictures are JPEGs with the 9-byte header" { $jpegOk }
    Check "Skeletons run at 25+ a second" { @(($skeletonMessages.Count -ge 75), "$([math]::Round($skeletonMessages.Count / 3)) per second") }
    $sample = if ($skeletonMessages.Count) { $skeletonMessages[-1] | ConvertFrom-Json } else { $null }
    Check "Two people are tracked with 20 joints each" {
        if (-not $sample) { return "no skeletons" }
        $joints = $sample.bodies | ForEach-Object { ($_.joints.PSObject.Properties | Measure-Object).Count }
        @(($sample.bodies.Count -eq 2 -and -not ($joints | Where-Object { $_ -ne 20 })), "joints per person: $($joints -join ', ')")
    }
    Check "People are numbered 1 and 2 in the order they appeared (issue #6)" { @((($sample.bodies.person -join ',') -eq '1,2'), "person numbers: $($sample.bodies.person -join ', ')") }

    Send $c '{"type":"skeleton.settings","v":1,"mode":"seated"}'
    $seated = $null; $end = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $end -and -not $seated) { $m = WaitFor $c '"type":"skeletons"' 1; if ($m -and $m.bodies.Count -and (($m.bodies[0].joints.PSObject.Properties | Measure-Object).Count -eq 10)) { $seated = $m } }
    Check "Seated mode tracks 10 upper-body joints" { [bool]$seated }
    Send $c '{"type":"skeleton.settings","v":1,"mode":"standing"}'
    Send $c '{"type":"subscribe","v":1,"streams":[]}'
    $null = Collect $c 0.5

    # ----- Commands -----
    Write-Host "Commands" -ForegroundColor Cyan
    Send $c 'not json'
    Check "A broken message gets a plain-English error" { [bool](WaitFor $c '"code":"badMessage"' 2) }
    Send $c '{"type":"fly","v":1}'
    Check "An unknown message gets an error" { [bool](WaitFor $c '"code":"unknownType"' 2) }

    Send $c '{"type":"tilt","v":1,"angle":5}'
    Start-Sleep -Milliseconds 200
    Send $c '{"type":"tilt","v":1,"angle":6}'
    Check "Tilt moves, and a second move straight away is refused" { [bool](WaitFor $c '"code":"tiltRefused"' 2) }

    Send $c '{"type":"snapshot","v":1}'
    $snap = WaitFor $c '"type":"snapshot"' 5
    Check "Snapshot saves two PNG files that download" {
        if (-not $snap) { return "no reply" }
        $pngs = $snap.files | ForEach-Object { $b = DownloadBytes ("$origin" + $_.url); $b[1] -eq 0x50 -and $b[2] -eq 0x4E -and $b[3] -eq 0x47 }
        @((@($pngs | Where-Object { $_ }).Count -eq 2), ($snap.files.name -join ', '))
    }
    Send $c '{"type":"snapshot","v":1}'
    Send $c '{"type":"snapshot","v":1}'
    $first = WaitFor $c '"type":"snapshot"' 5; $second = WaitFor $c '"type":"snapshot"' 5
    Check "Two snapshots in the same second keep all four files (issue #2)" {
        $names = @($first.files.name) + @($second.files.name) | Sort-Object -Unique
        @(($names.Count -eq 4), ($names -join ', '))
    }

    # ----- Motion capture -----
    Write-Host "Motion capture" -ForegroundColor Cyan
    Send $c '{"type":"mocap.start","v":1}'
    Check "Record starts a countdown" { [bool](WaitFor $c '"type":"mocap".*"state":"countdown"' 2) }
    Check "Recording starts after the countdown" { [bool](WaitFor $c '"type":"mocap".*"state":"recording"' 5) }
    Start-Sleep -Seconds 2
    Send $c '{"type":"mocap.stop","v":1}'
    $saved = WaitFor $c '"kind":"saved"' 10
    Check "Stop saves the take" { if ($saved) { @($true, $saved.event.id) } else { "no saved event" } }
    if ($saved) {
        $id = $saved.event.id
        Check "The take downloads as JSON with frames" {
            $take = (New-Object Net.WebClient).DownloadString("$origin/captures/mocap/$([Uri]::EscapeDataString($id)).json") | ConvertFrom-Json
            @(($take.format -eq 'kinect-web-console-take' -and $take.frames.Count -ge 45), "$($take.frames.Count) frames")
        }
        Send $c ('{"type":"export","v":1,"kind":"take","format":"bvh","id":"' + $id + '"}')
        $bvh = WaitFor $c '"type":"(export|error)"' 20
        Check "BVH export rebuilds the joints within 2 cm" {
            if (-not $bvh -or -not $bvh.note) { return "no export" }
            $cm = [double]([regex]::Match($bvh.note, 'average ([\d.]+) cm').Groups[1].Value)
            $head = [Text.Encoding]::ASCII.GetString((DownloadBytes ("$origin" + $bvh.url)), 0, 9)
            @(($cm -lt 2 -and $head -eq 'HIERARCHY'), "$cm cm")
        }
        # Issue #9: Mixamo names, a T-pose rest (forearm offset straight out along +X), feet kept on the floor
        Send $c ('{"type":"export","v":1,"kind":"take","format":"bvh","id":"' + $id + '","names":"mixamo"}')
        $mix = WaitFor $c '"type":"(export|error)"' 20
        Check "BVH with Mixamo names has a T-pose rest pose (issue #9)" {
            if (-not $mix -or -not $mix.url) { return "no export" }
            $text = (New-Object Net.WebClient).DownloadString("$origin" + $mix.url)
            $forearm = [regex]::Match($text, 'JOINT LeftForeArm\s*\{\s*OFFSET ([-\d.]+) ([-\d.]+) ([-\d.]+)')
            $x = [double]$forearm.Groups[1].Value; $y = [double]$forearm.Groups[2].Value
            @(($text -match 'ROOT Hips' -and $text -match 'JOINT LeftUpLeg' -and $x -gt 0.15 -and [math]::Abs($y) -lt 0.01), "LeftForeArm offset $x, $y; $($mix.name)")
        }

        # Issue #8: trimming keeps only part of the take for playback and export
        Send $c ('{"type":"mocap.trim","v":1,"id":"' + $id + '","start":0.5,"end":1.5}')
        $trimmed = WaitFor $c '"kind":"trimmed"' 5
        Send $c ('{"type":"export","v":1,"kind":"take","format":"bvh","id":"' + $id + '"}')
        $trimBvh = WaitFor $c '"type":"(export|error)"' 20
        $trimFrames = [int]([regex]::Match("$($trimBvh.note)", '^(\d+) frames').Groups[1].Value)
        Check "Trimming a take to 1 second makes a 1-second BVH (issue #8)" { @(($trimmed -and $trimFrames -ge 28 -and $trimFrames -le 32), "trim $($trimmed.event.trimStart) to $($trimmed.event.trimEnd) s, $trimFrames BVH frames") }
        Send $c ('{"type":"mocap.trim","v":1,"id":"' + $id + '","start":1.5,"end":0.5}')
        Check "A backwards trim is refused (issue #8)" { [bool](WaitFor $c '"code":"badTrim"' 5) }
        Send $c ('{"type":"mocap.rename","v":1,"id":"' + $id + '","name":"Self-test take"}')
        $renamed = WaitFor $c '"kind":"renamed"' 5
        Check "Rename works" { $renamed -and $renamed.event.id -eq 'Self-test take' }
        # (the take is kept for the restart checks at the end, then deleted there)
    }

    # ----- 3D scanning -----
    Write-Host "3D scanning" -ForegroundColor Cyan
    Send $c '{"type":"subscribe","v":1,"streams":["fusion"]}'
    Send $c '{"type":"fusion.preset","v":1,"preset":"room"}'
    Send $c '{"type":"fusion.start","v":1}'
    $messages = Collect $c 4
    $fusion = $messages | Where-Object { -not $_.Binary -and $_.Text -match '"type":"fusion"' } | Select-Object -Last 1 | ForEach-Object { $_.Text | ConvertFrom-Json }
    $previews = @($messages | Where-Object { $_.Binary -and $_.Type -eq 4 }).Count
    Check "Kinect Fusion scans and keeps tracking" { @(($fusion -and $fusion.state -eq 'scanning' -and $fusion.tracking -eq 'ok' -and $fusion.framesIntegrated -gt 30), "$($fusion.framesIntegrated) frames, $($fusion.processor)") }
    Check "The shaded preview streams" { @(($previews -ge 20), "$previews pictures in 4 s") }
    Send $c '{"type":"fusion.pause","v":1}'

    # Issue #3: an export must not hold up the tab's other messages, and only one export runs at a time
    Send $c '{"type":"export","v":1,"kind":"scan","format":"obj"}'
    Send $c '{"type":"export","v":1,"kind":"scan","format":"ply"}'
    Send $c '{"type":"fly","v":1}'
    $order = @(); $end = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $end -and -not ($order -contains 'export')) {
        $m = Receive $c 1000
        if ($m -and -not $m.Binary) {
            if ($m.Text -match '"code":"unknownType"') { $order += 'reply' }
            elseif ($m.Text -match '"type":"export"') { $order += 'export' }
            elseif ($m.Text -match 'already being made') { $order += 'refused' }
        }
    }
    Check "While a scan exports, the tab's other messages are answered first (issue #3)" { @(($order.IndexOf('reply') -ge 0 -and $order.IndexOf('reply') -lt $order.IndexOf('export')), ($order -join ' then ')) }
    Check "A second export at the same time is refused politely (issue #3)" { $order -contains 'refused' }

    # Issue #4: with nobody on the 3D scan tab, the scanner only sends a light status every 2 seconds
    Send $c '{"type":"subscribe","v":1,"streams":[]}'
    $null = Collect $c 0.6
    $quiet = @(Collect $c 4 | Where-Object { -not $_.Binary -and $_.Text -match '"type":"fusion"' }).Count
    Check "An unwatched scanner sends status only every 2 seconds (issue #4)" { @(($quiet -le 3), "$quiet status messages in 4 s") }

    foreach ($format in 'stl', 'obj', 'ply') {
        Send $c ('{"type":"export","v":1,"kind":"scan","format":"' + $format + '"}')
        $reply = WaitFor $c '"type":"(export|error)"' 60
        Check "Scan exports as $($format.ToUpper())" {
            if (-not $reply -or -not $reply.url) { return "no export" }
            $bytes = DownloadBytes ("$origin" + $reply.url)
            @(($bytes.Length -gt 10000), "$([math]::Round($bytes.Length / 1MB, 1)) MB")
        }
    }
    Close $c

    # ----- The page in a real (headless) browser -----
    Write-Host "The page in a browser" -ForegroundColor Cyan
    $edgePath = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $edgePath) {
        Report "Browser checks" $false "Microsoft Edge not found"
    } else {
        $cdpPort = $Port + 100
        $edge = Start-Process $edgePath -ArgumentList "--headless=new", "--remote-debugging-port=$cdpPort", "--user-data-dir=`"$work\edge`"", "--window-size=1400,820", "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "about:blank" -PassThru
        $target = $null
        for ($i = 0; $i -lt 40 -and -not $target; $i++) { Start-Sleep -Milliseconds 250; try { $target = (Invoke-RestMethod "http://127.0.0.1:$cdpPort/json") | Where-Object type -eq 'page' | Select-Object -First 1 } catch { } }
        $cdp = @{ Ws = (New-Object System.Net.WebSockets.ClientWebSocket); Pending = $null; Buffer = (New-Object byte[] (4MB)) }
        $cdp.Ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
        $script:cdpId = 0
        $problems = New-Object System.Collections.Generic.List[string]
        # Sends a DevTools command and returns its result; page errors seen meanwhile are collected
        function Cdp($method, $params = @{}) {
            $script:cdpId++
            Send $cdp (@{ id = $script:cdpId; method = $method; params = $params } | ConvertTo-Json -Depth 8 -Compress)
            while ($true) {
                $m = Receive $cdp 15000
                if (-not $m) { throw "the browser stopped answering" }
                $reply = $m.Text | ConvertFrom-Json
                if ($reply.method -eq 'Runtime.exceptionThrown') { $problems.Add($reply.params.exceptionDetails.exception.description) }
                if ($reply.method -eq 'Runtime.consoleAPICalled' -and $reply.params.type -eq 'error') { $problems.Add(($reply.params.args | ForEach-Object { $_.value }) -join ' ') }
                if ($reply.id -eq $script:cdpId) { return $reply.result }
            }
        }
        function Js($expression) { (Cdp "Runtime.evaluate" @{ expression = $expression; returnByValue = $true; awaitPromise = $true }).result.value }

        $null = Cdp "Runtime.enable"
        $null = Cdp "Page.enable"
        $null = Cdp "Browser.setDownloadBehavior" @{ behavior = "deny" }
        $null = Cdp "Page.navigate" @{ url = "$origin/#live" }
        Start-Sleep -Seconds 4
        Check "Live tab shows colour and depth at 20+ frames a second" {
            $fps = Js "[document.getElementById('fps-colour').textContent, document.getElementById('fps-depth').textContent]"
            $numbers = $fps | ForEach-Object { [int]($_ -replace '\D', '') }
            @((-not ($numbers | Where-Object { $_ -lt 20 })), ($fps -join ', '))
        }
        $null = Js "document.querySelector('input[value=cloud]').click()"
        Start-Sleep -Seconds 3
        Check "3D points view draws" { @(([bool](Js "!!document.querySelector('#cloud canvas')") -and (Js "document.getElementById('fps-cloud').textContent") -notmatch '^0'), (Js "document.getElementById('fps-cloud').textContent")) }
        $null = Js "document.querySelector('input[value=both]').click()"
        foreach ($tab in 'skeleton', 'mocap', 'scan', 'status') {
            $null = Js "document.querySelector('.tabs button[data-tab=$tab]').click()"
            Start-Sleep -Seconds 2
        }
        Check "Skeleton tab lists Person 1 then Person 2" { $people = Js "document.getElementById('skeleton-people').innerText"; @(($people -match '^Person 1.*Person 2'), ($people -replace "`n", '; ')) }
        Check "Status tab shows the mock sensor" { (Js "document.getElementById('st-mode').textContent") -match 'Mock' }

        # Issue #7: the mock's waving person holds a hand above their head, so hands-free should press Record by itself
        $null = Js "document.querySelector('.tabs button[data-tab=mocap]').click(); document.querySelector('.handsfree-switch').click(); 'on'"
        $seenProgress = $false; $started = $false; $end = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $end -and -not $started) {
            Start-Sleep -Milliseconds 300
            if (-not (Js "document.getElementById('handsfree-indicator').hidden")) { $seenProgress = $true }
            $started = (Js "document.getElementById('mocap-record').textContent") -match 'Stop'
        }
        Check "Hands-free: a raised hand shows progress, then starts recording (issue #7)" { @(($seenProgress -and $started), "progress shown: $seenProgress, recording started: $started") }
        $null = Js "document.getElementById('mocap-record').click(); document.querySelector('.handsfree-switch').click(); 'off'"
        Start-Sleep -Seconds 3   # the hands-free take saves
        $null = Js "[...document.querySelectorAll('.take-open')].find(b => b.textContent.includes('Self-test take')).click(); 'open'"
        Start-Sleep -Seconds 2
        Check "Opening a trimmed take shows the kept part (issue #8)" { $text = Js "document.getElementById('trim-text').textContent"; @(($text -match '^Keeps 0:00 to 0:01'), $text) }
        $null = Js "document.getElementById('back-live').click()"
        $null = Js "1"   # collect any last errors
        Check "No script errors on any tab" { if ($problems.Count) { "$($problems.Count): " + ($problems | Select-Object -First 2) -join ' | ' } else { $true } }
        Close $cdp
        Stop-Process -Id $edge.Id -ErrorAction SilentlyContinue
    }

    # ----- Choices survive a restart (issue #1) -----
    Write-Host "Remembered choices" -ForegroundColor Cyan
    $c = New-Client
    $null = WaitFor $c '"type":"status"' 3
    foreach ($m in '{"type":"skeleton.settings","v":1,"mode":"seated","smoothing":"heavy"}', '{"type":"live.settings","v":1,"peopleHighlight":true}',
                   '{"type":"fusion.preset","v":1,"preset":"person"}', '{"type":"fusion.colour","v":1,"on":true}') { Send $c $m }
    Start-Sleep -Seconds 1.5   # preferences are saved half a second after the last change
    Close $c
    Stop-Process -Id $bridge.Id; $bridge.WaitForExit(5000) | Out-Null
    $up = Start-Bridge "bridge-output-restart.txt"
    Check "The bridge restarts" { $up }
    if ($up) {
        $c = New-Client
        $status = WaitFor $c '"type":"status"' 3
        $fusion = WaitFor $c '"type":"fusion"' 3
        Check "Skeleton mode and smoothing are remembered" { @(($status.skeleton.mode -eq 'seated' -and $status.skeleton.smoothing -eq 'heavy'), "$($status.skeleton.mode), $($status.skeleton.smoothing)") }
        Check "People highlight is remembered" { $status.live.peopleHighlight -eq $true }
        Check "Scan preset and colour are remembered" { @(($fusion.preset -eq 'person' -and $fusion.colour -eq $true), "$($fusion.preset), colour $($fusion.colour)") }

        # Issue #5: after a restart the take list comes from the index, without reading each take in full
        $found = Get-Content "$work\bridge-output-restart.txt" | Select-String "Found \d+ saved takes" | Select-Object -First 1
        $fresh = New-Client
        $mocap = WaitFor $fresh '"type":"mocap".*"takes"' 3
        Close $fresh
        Check "The take list is rebuilt from the index at start-up (issue #5)" { @(($found.Line -match '\(0 read in full' -and ($mocap.takes.name -contains 'Self-test take')), ($found.Line -replace '^.*INFO\s+', '')) }
        Send $c '{"type":"mocap.delete","v":1,"id":"Self-test take"}'
        Check "Delete works" { [bool](WaitFor $c '"kind":"deleted"' 5) }
        Close $c
    }
}
catch {
    Report "Self-test ran to the end" $false $_.Exception.Message
}
finally {
    if ($edge) { Stop-Process -Id $edge.Id -ErrorAction SilentlyContinue; Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match [regex]::Escape($work) } | Stop-Process -ErrorAction SilentlyContinue }
    if ($bridge) { Stop-Process -Id $bridge.Id -ErrorAction SilentlyContinue }
    $bridgeErrors = @(Get-ChildItem "$work\bridge-output*.txt" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName } | Select-String " ERROR ")
    if ($bridgeErrors.Count) { Report "No errors in the bridge log" $false ($bridgeErrors[0].Line) } else { Report "No errors in the bridge log" $true $null }
}

$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host ("All {0} checks passed." -f $results.Count) -ForegroundColor Green
    if (-not $Keep) { Start-Sleep -Milliseconds 500; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
} else {
    Write-Host ("{0} of {1} checks failed. Details are above; the bridge's output is in {2}" -f $failed.Count, $results.Count, "$work\bridge-output.txt") -ForegroundColor Red
}
exit $failed.Count
