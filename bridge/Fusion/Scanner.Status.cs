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
    /// <summary>3D scanning, part: the status message (state, coverage, advice, recent files).</summary>
    partial class Scanner
    {
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
                // In turntable mode, only what the filter keeps counts as in range
                var keep = turntable ? MakeFilter(preset) : null;
                int inRange = 0, tooClose = 0, total = 0;
                // Every 4th pixel each way is plenty for a percentage
                for (int y = 0; y < Height; y += 4)
                    for (int x = 0; x < Width; x += 4)
                    {
                        int mm = depth.Depth[y * Width + x];
                        total++;
                        if (mm >= near && mm <= far && (keep == null || keep.Keep(x, y, mm))) inRange++;
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
            bool col, tt;
            int found;
            int lost, frames;
            lock (gate)
            {
                s = state; preset = presetName; error = lastError; warning = processorWarning; processor = processorText;
                col = colour; tt = turntable; lost = lostFrames; frames = integrated; id = scanId; found = recovered;
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
                ["turntable"] = tt,
                ["recovered"] = found,
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
    }
}
