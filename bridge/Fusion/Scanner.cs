using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using KinectBridge.Sensor;
using KinectBridge.Skeleton;
using KinectBridge.Streams;
using KinectBridge.Web;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit.Fusion;

namespace KinectBridge.Fusion
{
    /// <summary>
    /// 3D scanning with Kinect Fusion. Each depth frame is lined up against the model built so far, which works out
    /// how the Kinect has moved, and then merged into it. Moving the Kinect slowly around something (or turning the
    /// thing in front of it) fills in the model from every side.
    ///
    /// Runs on the graphics card (DirectX 11) when it can, otherwise on the processor, which is much slower.
    /// Sends a shaded picture of the model from the Kinect's current viewpoint as stream 4 ("fusion"),
    /// and a "fusion" status message (docs/PROTOCOL.md).
    /// </summary>
    class Scanner : IDisposable
    {
        const int Width = DepthFrame.Width, Height = DepthFrame.Height;
        const int LostAfterFrames = 8;          // this many failed line-ups in a row count as lost tracking
        const int PreviewEveryMs = 66;          // about 15 preview pictures a second
        const int StatusEveryMs = 250;
        const int CpuMaxVoxelsPerSide = 256;    // the processor cannot keep up with bigger volumes

        enum State { Idle, Scanning, Paused }

        readonly ISensor sensor;
        readonly StreamPump pump;
        readonly SkeletonPump skeletons;
        readonly MessageHub hub;
        readonly Dictionary<string, ScanPreset> presets;
        readonly string scansFolder;
        readonly object gate = new object();     // one thing at a time touches the Fusion volume
        readonly StreamWorker worker;
        readonly Timer statusTimer;
        readonly JpegEncoder previewJpeg;

        // The Fusion volume and its working images, created on first use
        ColorReconstruction volume;
        ScanPreset volumePreset;
        string processorText = "";
        string processorWarning;
        FusionFloatImageFrame depthFloat;
        FusionPointCloudImageFrame pointCloud;
        FusionColorImageFrame colourInput, shadedSurface, shadedColour;
        readonly DepthImagePixel[] pixels = new DepthImagePixel[Width * Height];
        readonly int[] colourIndex = new int[Width * Height];
        readonly int[] colourInts = new int[Width * Height];
        readonly int[] previewInts = new int[Width * Height];
        readonly byte[] previewBytes = new byte[Width * Height * 4];
        Matrix4 worldToCamera = Matrix4.Identity;
        Matrix4 defaultWorldToVolume;

        State state = State.Idle;
        string presetName = "object";
        bool colour;
        bool colourUsed;                         // colour was on for at least part of this scan
        int lostFrames;
        int integrated;
        string scanId;
        MotionReading gravity;                   // accelerometer at the start of the scan, for levelling exports
        string placement = "";                   // where the box sits, in words, for the page
        int exporting;                           // 1 while an export is being made (one at a time)
        List<object> cachedFiles;                // the recent exports list; cleared when a new file is saved
        int statusTicks;
        string lastError;
        readonly Stopwatch previewClock = Stopwatch.StartNew();
        int processedCount;
        double fps;
        readonly Stopwatch fpsClock = Stopwatch.StartNew();

        public Scanner(ISensor sensor, StreamPump pump, SkeletonPump skeletons, MessageHub hub, AppSettings settings)
        {
            this.sensor = sensor;
            this.pump = pump;
            this.skeletons = skeletons;
            this.hub = hub;
            presets = ScanPresets.Load(settings.Fusion);
            scansFolder = Path.Combine(settings.CapturesPath, "scans");
            Directory.CreateDirectory(Path.Combine(scansFolder, "preview"));
            previewJpeg = new JpegEncoder(Width, Height, settings.JpegQuality);
            worker = new StreamWorker("3D scan", Process);
            pump.DepthArrived += () => { if (state == State.Scanning) worker.Signal(); };
            sensor.StateChanged += OnSensorState;
            statusTimer = new Timer(_ => StatusTick(), null, StatusEveryMs, StatusEveryMs);
        }

        // ----- Controls (from the page) -----

        /// <summary>Starts or resumes scanning. Returns null, or a plain-English reason it cannot.</summary>
        public string Start()
        {
            lock (gate)
            {
                if (sensor.State != SensorState.Ready) return "The Kinect is not ready, so there is nothing to scan. The status light says why.";
                if (state == State.Scanning) return null;
                var error = EnsureVolume();
                if (error != null) return error;
                if (state == State.Idle) NewScan();
                state = State.Scanning;
                lastError = null;
            }
            Log.Info($"3D scan: scanning ({presets[presetName].Label} preset)");
            Broadcast();
            return null;
        }

        public void Pause()
        {
            lock (gate)
            {
                if (state != State.Scanning) return;
                state = State.Paused;
                RenderPreview();   // leave the latest picture on screen
            }
            Log.Info($"3D scan: paused after {integrated} frames");
            Broadcast();
        }

        /// <summary>Clears the model and starts again from where the Kinect is now. Keeps scanning if it was.</summary>
        public void Reset()
        {
            lock (gate)
            {
                if (volume != null) NewScan();
                if (state == State.Paused) state = State.Idle;
            }
            Log.Info("3D scan: reset");
            Broadcast();
        }

        /// <summary>Switches preset. The model is cleared, because the volume has to be rebuilt at the new size.</summary>
        public string SetPreset(string name)
        {
            if (name == null || !presets.ContainsKey(name)) return "That is not one of the scan presets.";
            lock (gate)
            {
                if (name == presetName) return null;
                presetName = name;
                DisposeVolume();
                state = State.Idle;
                integrated = 0;
                scanId = null;
            }
            Log.Info($"3D scan: preset {presets[name].Label}");
            Broadcast();
            return null;
        }

        /// <summary>Puts back last run's preset and colour setting, before anything is scanned.</summary>
        public void Restore(string preset, bool colourOn)
        {
            lock (gate)
            {
                if (preset != null && presets.ContainsKey(preset)) presetName = preset;
                colour = colourOn;
            }
        }

        public void SetColour(bool on)
        {
            lock (gate) colour = on;
            Log.Info($"3D scan: colour capture {(on ? "on" : "off")}");
            Broadcast();
        }

        // ----- The Fusion volume -----

        /// <summary>Creates the volume for the current preset if needed: graphics card first, processor if that fails.</summary>
        string EnsureVolume()
        {
            var preset = presets[presetName];
            if (volume != null && volumePreset == preset) return null;
            DisposeVolume();

            var gpu = BestGraphicsCard(out var gpuName);
            if (gpu >= 0)
            {
                try
                {
                    volume = ColorReconstruction.FusionCreateReconstruction(Parameters(preset, false), ReconstructionProcessor.Amp, gpu, Matrix4.Identity);
                    processorText = "Graphics card: " + gpuName;
                    processorWarning = null;
                }
                catch (Exception ex)
                {
                    Log.Warn($"3D scan: the graphics card could not run Kinect Fusion ({ex.Message}); using the processor instead");
                }
            }
            if (volume == null)
            {
                try
                {
                    volume = ColorReconstruction.FusionCreateReconstruction(Parameters(preset, true), ReconstructionProcessor.Cpu, -1, Matrix4.Identity);
                    processorText = "Processor (no suitable graphics card)";
                    processorWarning = "Kinect Fusion is running on the processor because no DirectX 11 graphics card could be used. It will be slow and less detailed: move the Kinect very slowly.";
                }
                catch (Exception ex)
                {
                    Log.Error("3D scan: Kinect Fusion could not start: " + ex.Message);
                    return "Kinect Fusion could not start. Check the Kinect Developer Toolkit is installed (docs\\SETUP.md). The bridge log has details.";
                }
            }

            volumePreset = preset;
            depthFloat = new FusionFloatImageFrame(Width, Height);
            pointCloud = new FusionPointCloudImageFrame(Width, Height);
            colourInput = new FusionColorImageFrame(Width, Height);
            shadedSurface = new FusionColorImageFrame(Width, Height);
            shadedColour = new FusionColorImageFrame(Width, Height);
            defaultWorldToVolume = volume.GetCurrentWorldToVolumeTransform();
            Log.Info($"3D scan: {preset.Label} volume ready on {processorText}, {preset.SizeX:0.00} x {preset.SizeY:0.00} x {preset.SizeZ:0.00} m at {preset.VoxelMm:0.0} mm detail");
            return null;
        }

        /// <summary>The volume size. On the processor, fewer voxels over the same space, so it can keep up.</summary>
        static ReconstructionParameters Parameters(ScanPreset p, bool cpu)
        {
            if (!cpu) return new ReconstructionParameters(p.VoxelsPerMeter, p.VoxelsX, p.VoxelsY, p.VoxelsZ);
            var shrink = Math.Min(1.0, CpuMaxVoxelsPerSide / (double)Math.Max(p.VoxelsX, Math.Max(p.VoxelsY, p.VoxelsZ)));
            int Side(int v) => Math.Max(32, (int)(v * shrink) / 32 * 32);
            return new ReconstructionParameters((float)(p.VoxelsPerMeter * shrink), Side(p.VoxelsX), Side(p.VoxelsY), Side(p.VoxelsZ));
        }

        /// <summary>The DirectX 11 device with the most memory (the dedicated card rather than one built into the processor), or -1.</summary>
        static int BestGraphicsCard(out string name)
        {
            name = null;
            int best = -1, bestMemory = -1;
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    FusionDepthProcessor.GetDeviceInfo(ReconstructionProcessor.Amp, i, out var description, out _, out var memoryKb);
                    // Windows also lists a software "Basic Render Driver", which is really the processor
                    if (description.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (memoryKb > bestMemory) { best = i; bestMemory = memoryKb; name = description; }
                }
                catch { break; }   // no more devices
            }
            return best;
        }

        /// <summary>Clears the model and starts a new scan from where the Kinect is now.</summary>
        void NewScan()
        {
            worldToCamera = Matrix4.Identity;
            // By default the Kinect sits at the middle of the box's front face (voxel = metres x voxels-per-metre + half the box).
            // Push the box out to the preset's start distance.
            var worldToVolume = defaultWorldToVolume;
            var vpm = volumePreset.VoxelsPerMeter;
            worldToVolume.M43 -= volumePreset.StartDistance * vpm;
            placement = "centred on where the Kinect points";

            // Stand the box on the floor, if the preset asks and the Kinect can see the floor. Fusion's "down" is
            // down the picture, and the skeleton tracker's floor plane gives the Kinect's height above the floor.
            var floor = skeletons.Latest?.Floor;
            if (volumePreset.OnFloor && floor != null && floor[1] > 0.8)
            {
                var height = floor[3] / Math.Sqrt(floor[0] * floor[0] + floor[1] * floor[1] + floor[2] * floor[2]);
                if (height > 0.2 && height < 2.5)
                {
                    const double margin = 0.05;   // a little below the floor, so feet and chair legs are never clipped
                    worldToVolume.M42 = (float)(volumePreset.VoxelsY - (height + margin) * vpm);
                    placement = $"standing on the floor, {height:0.00} m below the Kinect";
                }
            }
            else if (volumePreset.OnFloor)
            {
                placement = "centred on where the Kinect points (it cannot see the floor, so the box could not be stood on it)";
            }
            volume.ResetReconstruction(worldToCamera, worldToVolume);
            integrated = 0;
            lostFrames = 0;
            colourUsed = false;
            scanId = "scan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + presetName;
            gravity = sensor.ReadMotion();
        }

        void DisposeVolume()
        {
            foreach (var d in new IDisposable[] { volume, depthFloat, pointCloud, colourInput, shadedSurface, shadedColour }) d?.Dispose();
            volume = null;
            volumePreset = null;
        }

        void OnSensorState()
        {
            if (sensor.State == SensorState.Ready) return;
            lock (gate)
            {
                if (state != State.Scanning) return;
                state = State.Paused;
                lastError = "Scanning paused because the Kinect stopped. Press Start to carry on once the status light is green.";
            }
            Log.Warn("3D scan: paused because the Kinect stopped");
            Broadcast();
        }

        // ----- Each depth frame -----

        void Process()
        {
            lock (gate)
            {
                if (state != State.Scanning || volume == null) return;
                var depth = pump.BorrowDepth();
                if (depth == null) return;
                try
                {
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = new DepthImagePixel { Depth = depth.Depth[i], PlayerIndex = depth.Player[i] };
                    volume.DepthToDepthFloatFrame(pixels, depthFloat, volumePreset.MinDepth, volumePreset.MaxDepth, false);

                    bool tracked;
                    try
                    {
                        tracked = colour && PrepareColour(depth)
                            ? volume.ProcessFrame(depthFloat, colourInput, FusionDepthProcessor.DefaultAlignIterationCount,
                                FusionDepthProcessor.DefaultIntegrationWeight, FusionDepthProcessor.DefaultColorIntegrationOfAllAngles, worldToCamera)
                            : volume.ProcessFrame(depthFloat, FusionDepthProcessor.DefaultAlignIterationCount,
                                FusionDepthProcessor.DefaultIntegrationWeight, worldToCamera);
                    }
                    catch (InvalidOperationException)
                    {
                        tracked = false;   // Fusion's way of saying the frame could not be lined up
                    }

                    if (tracked)
                    {
                        worldToCamera = volume.GetCurrentWorldToCameraTransform();
                        integrated++;
                        if (lostFrames >= LostAfterFrames) Log.Info("3D scan: tracking found again");
                        lostFrames = 0;
                        if (colour) colourUsed = true;
                    }
                    else if (++lostFrames == LostAfterFrames)
                    {
                        Log.Warn("3D scan: tracking lost");
                    }

                    processedCount++;
                    if (previewClock.ElapsedMilliseconds >= PreviewEveryMs)
                    {
                        previewClock.Restart();
                        RenderPreview();
                    }
                }
                finally
                {
                    depth.Release();
                }
            }
        }

        /// <summary>Fills the colour image lined up with the depth image. False if no colour is available.</summary>
        bool PrepareColour(DepthFrame depth)
        {
            var frame = pump.BorrowColour();
            if (frame == null) return false;
            try
            {
                if (!sensor.MapDepthToColour(depth, colourIndex)) return false;
                var bgra = frame.Pixels;
                for (int i = 0; i < colourIndex.Length; i++)
                {
                    var c = colourIndex[i];
                    colourInts[i] = c < 0 ? 0 : bgra[c * 4] | bgra[c * 4 + 1] << 8 | bgra[c * 4 + 2] << 16 | 255 << 24;
                }
                colourInput.CopyPixelDataFrom(colourInts);
                return true;
            }
            finally
            {
                frame.Release();
            }
        }

        /// <summary>Draws the model as seen from the Kinect right now, shaded (or in colour), and sends it to the page.</summary>
        void RenderPreview()
        {
            if (volume == null || !hub.AnySubscribed("fusion")) return;
            if (colourUsed)
            {
                volume.CalculatePointCloud(pointCloud, shadedColour, worldToCamera);
                shadedColour.CopyPixelDataTo(previewInts);
            }
            else
            {
                volume.CalculatePointCloud(pointCloud, worldToCamera);
                FusionDepthProcessor.ShadePointCloud(pointCloud, worldToCamera, shadedSurface, null);
                shadedSurface.CopyPixelDataTo(previewInts);
            }
            Buffer.BlockCopy(previewInts, 0, previewBytes, 0, previewBytes.Length);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            hub.BroadcastBinary("fusion", previewJpeg.Encode(previewBytes, StreamHeader.FusionPreview, timestamp));
        }

        // ----- Export -----

        /// <summary>
        /// Saves the model as stl, obj or ply in captures\scans, or as a lighter "preview" PLY for the page's 3D view.
        /// Returns the file name and the writer's summary. Throws InvalidOperationException with a plain-English message.
        /// </summary>
        public (string name, string url, MeshWriter.Result result) Export(string format, MeshWriter.Cleanup cleanup)
        {
            if (Interlocked.CompareExchange(ref exporting, 1, 0) != 0)
                throw new InvalidOperationException("An export is already being made. Wait for it to finish, then try again.");
            try
            {
                ColorMesh mesh;
                string fileName, relative, path;
                bool preview, withColour;
                MotionReading level;

                // Only reading the mesh out of the volume needs scanning to wait; writing the file happens after
                lock (gate)
                {
                    if (volume == null || integrated == 0)
                        throw new InvalidOperationException("There is no scan to export yet. Press Start and move the Kinect slowly around what you are scanning.");

                    preview = format == "preview";
                    var step = preview ? Math.Max(2, volumePreset.MeshVoxelStep) : volumePreset.MeshVoxelStep;
                    fileName = scanId + "." + (preview ? "ply" : format);
                    relative = preview ? "preview/" + fileName : fileName;
                    path = Path.Combine(scansFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                    withColour = colourUsed;
                    level = gravity;
                    mesh = volume.CalculateMesh(step);
                }

                using (mesh)
                {
                    var result = MeshWriter.Write(mesh, preview ? "ply" : format, path, withColour, level, cleanup);
                    if (!preview)
                    {
                        Log.Info($"3D scan: saved {fileName}: {result.Triangles:N0} triangles, {result.SizeX:0.00} x {result.SizeY:0.00} x {result.SizeZ:0.00} m");
                        Volatile.Write(ref cachedFiles, null);   // the list of exports has changed
                    }
                    return (fileName, "/captures/scans/" + relative.Split('/').Select(Uri.EscapeDataString).Aggregate((a, b) => a + "/" + b), result);
                }
            }
            finally
            {
                Volatile.Write(ref exporting, 0);
            }
        }

        // ----- Status -----

        const int KinectNearestMm = 800;   // the Xbox 360 Kinect cannot measure anything closer
        const double EnoughInRange = 0.15;  // below this share of the picture, Fusion struggles to lock on

        /// <summary>
        /// How much of the newest depth picture falls inside the preset's scanning range, and how much is too close
        /// to measure. Fusion needs a good share of the picture in range to keep track of where the Kinect is.
        /// </summary>
        (double inRange, double tooClose) Coverage(ScanPreset preset)
        {
            var depth = pump.BorrowDepth();
            if (depth == null) return (0, 0);
            try
            {
                int near = (int)(Math.Max(preset.MinDepth, KinectNearestMm / 1000f) * 1000);
                int far = (int)(Math.Min(preset.MaxDepth, preset.StartDistance + preset.SizeZ) * 1000);
                int inRange = 0, tooClose = 0, total = 0;
                // Every 4th pixel each way is plenty for a percentage
                for (int y = 0; y < Height; y += 4)
                    for (int x = 0; x < Width; x += 4)
                    {
                        int mm = depth.Depth[y * Width + x];
                        total++;
                        if (mm >= near && mm <= far) inRange++;
                        else if (mm > 0 && mm < KinectNearestMm) tooClose++;
                    }
                return (inRange / (double)total, tooClose / (double)total);
            }
            finally
            {
                depth.Release();
            }
        }

        /// <summary>Plain-English advice when too little is in range, or null when it looks fine.</summary>
        static string CoverageHint(ScanPreset p, double inRange, double tooClose)
        {
            if (inRange >= EnoughInRange) return null;
            var range = $"{Math.Max(0.8, p.StartDistance):0.0} to {Math.Min(p.MaxDepth, p.StartDistance + p.SizeZ):0.0} m";
            if (tooClose > 0.3)
                return $"Too close: much of the picture is nearer than 0.8 m, which the Kinect cannot measure. Move back so what you are scanning is {range} from the Kinect.";
            return $"Very little is in the scanning range ({range} from the Kinect for the {p.Label} preset). Point the Kinect at what you are scanning, from that distance, or pick a bigger preset.";
        }

        /// <summary>
        /// Status four times a second while a page is on the 3D scan tab (it subscribes to "fusion"); otherwise
        /// only every 2 seconds and without measuring the depth picture, so an unwatched scanner costs almost nothing.
        /// </summary>
        void StatusTick()
        {
            var watched = hub.AnySubscribed("fusion");
            if (watched || state == State.Scanning || ++statusTicks % 8 == 0) Broadcast(watched);
        }

        /// <summary>The status message. Coverage (how much is in range) is only measured when someone is watching.</summary>
        public string Message(bool withCoverage = true)
        {
            State s;
            string preset, error, warning, processor, id;
            bool col;
            int lost, frames;
            lock (gate)
            {
                s = state; preset = presetName; error = lastError; warning = processorWarning; processor = processorText;
                col = colour; lost = lostFrames; frames = integrated; id = scanId;
                var seconds = fpsClock.Elapsed.TotalSeconds;
                if (seconds >= 1)
                {
                    fps = Math.Round(processedCount / seconds, 1);
                    processedCount = 0;
                    fpsClock.Restart();
                }
            }

            var current = presets[preset];
            var (inRange, tooClose) = withCoverage && sensor.State == SensorState.Ready ? Coverage(current) : (0.0, 0.0);

            return Json.Serialize(new Dictionary<string, object>
            {
                ["type"] = "fusion",
                ["v"] = MessageHub.ProtocolVersion,
                ["state"] = s.ToString().ToLowerInvariant(),
                ["preset"] = preset,
                ["presets"] = ScanPresets.Order.Select(n => presets[n]).Select(p => new
                {
                    name = p.Name, label = p.Label,
                    size = new[] { Math.Round(p.SizeX, 2), Math.Round(p.SizeY, 2), Math.Round(p.SizeZ, 2) },
                    detailMm = Math.Round(p.VoxelMm, 1), startDistance = p.StartDistance,
                }).ToList(),
                ["tracking"] = s != State.Scanning ? "idle" : lost >= LostAfterFrames ? "lost" : "ok",
                ["framesIntegrated"] = frames,
                ["fps"] = s == State.Scanning ? fps : 0,
                ["colour"] = col,
                ["processor"] = processor,
                ["processorWarning"] = warning,
                ["error"] = error,
                ["scanId"] = id,
                ["files"] = RecentFiles(),
                ["inRange"] = (int)Math.Round(inRange * 100),
                ["tooClose"] = (int)Math.Round(tooClose * 100),
                ["placement"] = placement,
                ["hint"] = withCoverage && sensor.State == SensorState.Ready ? CoverageHint(current, inRange, tooClose) : null,
            });
        }

        void Broadcast(bool withCoverage = true)
        {
            try { hub.Broadcast(Message(withCoverage), "fusion"); }
            catch (Exception ex) { Log.Error("3D scan status failed: " + ex.Message); }
        }

        /// <summary>The newest exported files, for the page's list of downloads. Read from disk only after a change.</summary>
        List<object> RecentFiles()
        {
            var cached = Volatile.Read(ref cachedFiles);
            if (cached != null) return cached;
            try
            {
                cached = new DirectoryInfo(scansFolder).GetFiles()
                    .Where(f => f.Extension == ".stl" || f.Extension == ".obj" || f.Extension == ".ply")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(12)
                    .Select(f => (object)new { name = f.Name, sizeMb = Math.Round(f.Length / 1048576.0, 1), url = "/captures/scans/" + Uri.EscapeDataString(f.Name) })
                    .ToList();
                Volatile.Write(ref cachedFiles, cached);
                return cached;
            }
            catch
            {
                return new List<object>();
            }
        }

        public void Dispose()
        {
            statusTimer.Dispose();
            worker.Dispose();
            lock (gate) DisposeVolume();
        }
    }
}
