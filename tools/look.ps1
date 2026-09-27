# Claude's "eyes": grabs what the Kinect sees right now through the running bridge.
# Saves the colour picture and the coloured depth picture in captures\look, and prints a short summary:
# the sensor state, the range of distances, who is tracked and where, and the floor.
#
# Usage (with the bridge running):   powershell -File tools\look.ps1 [-Name look]
# Handy for checking a set-up without being at the PC, for example whether a scan subject is in range.
param([string]$Name = "look")

$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path)
$port = 8766
try { $port = (Get-Content (Join-Path $root "settings.json") -Raw | ConvertFrom-Json).port } catch { }
$out = Join-Path $root "captures\look"
New-Item -ItemType Directory -Force $out | Out-Null

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.Options.SetRequestHeader("Origin", "http://localhost:$port")
try { $ws.ConnectAsync([Uri]"ws://localhost:$port/ws", [Threading.CancellationToken]::None).Wait() }
catch { "The bridge is not running. Double-click run.cmd first."; exit 1 }

$message = [Text.Encoding]::UTF8.GetBytes('{"type":"subscribe","v":1,"streams":["colour","depth","depthRaw","skeletons"]}')
$ws.SendAsync([ArraySegment[byte]]::new($message), 'Text', $true, [Threading.CancellationToken]::None).Wait()

# Collect one of each picture, a status and a skeleton frame (or give up after 5 seconds)
$buffer = [byte[]]::new(4MB); $pictures = @{}; $status = $null; $skeletons = $null
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline -and ($pictures.Count -lt 3 -or -not $skeletons -or -not $status)) {
    $ms = [IO.MemoryStream]::new()
    do {
        $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $ms.Write($buffer, 0, $r.Count)
    } until ($r.EndOfMessage)
    $data = $ms.ToArray()
    if ($r.MessageType -eq 'Binary') { $pictures[[int]$data[0]] = $data; continue }
    $text = [Text.Encoding]::UTF8.GetString($data)
    if ($text -match '"type":"status"') { $status = $text | ConvertFrom-Json }
    elseif ($text -match '"type":"skeletons"') { $skeletons = $text | ConvertFrom-Json }
}
$ws.Abort()

# Binary messages start with a 9-byte header (docs/PROTOCOL.md); the JPEG follows
if ($pictures[1]) { [IO.File]::WriteAllBytes("$out\$Name-colour.jpg", $pictures[1][9..($pictures[1].Length - 1)]) }
if ($pictures[2]) { [IO.File]::WriteAllBytes("$out\$Name-depth.jpg", $pictures[2][9..($pictures[2].Length - 1)]) }

if ($status) { "Sensor: $($status.sensor.title); tilt $($status.tilt.angle) degrees" }

if ($pictures[3]) {
    # Raw depth: 320x240 distances in millimetres; sample every other one
    $raw = $pictures[3]; $count = ($raw.Length - 9) / 2; $none = 0; $near = 0
    $values = [System.Collections.Generic.List[int]]::new()
    for ($i = 0; $i -lt $count; $i += 2) {
        $mm = [BitConverter]::ToUInt16($raw, 9 + 2 * $i)
        if ($mm -eq 0) { $none++ } elseif ($mm -lt 800) { $near++ } else { $values.Add($mm) }
    }
    $total = [math]::Ceiling($count / 2); $values.Sort()
    $noneShare = [math]::Round(100 * $none / $total); $nearShare = [math]::Round(100 * $near / $total)
    if ($values.Count) {
        "Depth: nearest {0:0.00} m, middle {1:0.00} m, furthest {2:0.00} m; {3}% no reading, {4}% closer than 0.8 m" -f `
            ($values[0] / 1000), ($values[[int]($values.Count / 2)] / 1000), ($values[$values.Count - 1] / 1000), $noneShare, $nearShare
    } else {
        "Depth: nothing measurable ({0}% no reading, {1}% closer than 0.8 m)" -f $noneShare, $nearShare
    }
}

if ($skeletons) {
    "People tracked: $($skeletons.bodies.Count)"
    foreach ($body in $skeletons.bodies) {
        $head = $body.joints.head
        if ($head) {
            $side = if ($head.p[0] -ge 0) { 'right' } else { 'left' }
            "  person {0}: head {1:0.00} m away, {2:0.00} m {3} of centre (as the picture shows it)" -f $body.player, $head.p[2], [math]::Abs($head.p[0]), $side
        }
    }
    if ($skeletons.floor) { "Floor: {0:0.00} m below the Kinect" -f $skeletons.floor[3] } else { "Floor: not in view" }
}
"Saved captures\look\$Name-colour.jpg and $Name-depth.jpg"
