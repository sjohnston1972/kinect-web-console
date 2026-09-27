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
    ///
    /// Split by job: this file has the set-up and the controls; Scanner.Volume.cs the Fusion volume;
    /// Scanner.Frames.cs each depth frame; Scanner.Export.cs saving meshes; Scanner.Status.cs the status message.
    /// </summary>
    partial class Scanner : IDisposable
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
        bool turntable;                          // turntable mode: only depth inside the box and above the floor
        TurntableFilter filter;                  // set while scanning in turntable mode
        bool colourUsed;                         // colour was on for at least part of this scan
        int lostFrames;
        Relocator relocator;                     // finds the Kinect's place again after tracking is lost (null if unavailable)
        int recovered;                           // times tracking was found again this scan by recognising the view
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
        public void Restore(string preset, bool colourOn, bool turntableOn)
        {
            lock (gate)
            {
                if (preset != null && presets.ContainsKey(preset)) presetName = preset;
                colour = colourOn;
                turntable = turntableOn;
            }
        }

        /// <summary>Turntable mode on or off. Applies from the next frame; the model so far is kept.</summary>
        public void SetTurntable(bool on)
        {
            lock (gate)
            {
                turntable = on;
                filter = on && volumePreset != null ? MakeFilter(volumePreset) : null;
            }
            Log.Info($"3D scan: turntable mode {(on ? "on" : "off")}");
            Broadcast();
        }

        /// <summary>The floor the skeleton tracker sees now: the plane, and the Kinect's height above it (or nulls).</summary>
        (float[] plane, double? height) FloorNow()
        {
            var floor = skeletons.Latest?.Floor;
            if (floor == null || floor[1] <= 0.8) return (null, null);
            var height = floor[3] / Math.Sqrt(floor[0] * floor[0] + floor[1] * floor[1] + floor[2] * floor[2]);
            return height > 0.2 && height < 2.5 ? (floor, height) : (floor, (double?)null);
        }

        TurntableFilter MakeFilter(ScanPreset preset)
        {
            var (plane, height) = FloorNow();
            return new TurntableFilter(preset, plane, preset.OnFloor ? height : null);
        }

        public void SetColour(bool on)
        {
            lock (gate) colour = on;
            Log.Info($"3D scan: colour capture {(on ? "on" : "off")}");
            Broadcast();
        }

        public void Dispose()
        {
            statusTimer.Dispose();
            worker.Dispose();
            lock (gate) DisposeVolume();
        }
    }
}
