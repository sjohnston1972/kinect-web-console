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
    /// <summary>3D scanning, part: the Kinect Fusion volume (making it on the graphics card or processor, placing the box, clearing it).</summary>
    partial class Scanner
    {
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
            try { relocator = new Relocator(Width, Height); }
            catch (Exception ex) { relocator = null; Log.Warn("3D scan: automatic recovery after lost tracking is not available: " + ex.Message); }
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
            var floorHeight = FloorNow().height;
            if (volumePreset.OnFloor && floorHeight != null)
            {
                const double margin = 0.05;   // a little below the floor, so feet and chair legs are never clipped
                worldToVolume.M42 = (float)(volumePreset.VoxelsY - (floorHeight.Value + margin) * vpm);
                placement = $"standing on the floor, {floorHeight.Value:0.00} m below the Kinect";
            }
            else if (volumePreset.OnFloor)
            {
                placement = "centred on where the Kinect points (it cannot see the floor, so the box could not be stood on it)";
            }
            volume.ResetReconstruction(worldToCamera, worldToVolume);
            relocator?.Reset();
            recovered = 0;
            integrated = 0;
            lostFrames = 0;
            colourUsed = false;
            filter = turntable ? MakeFilter(volumePreset) : null;
            scanId = "scan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + presetName;
            gravity = sensor.ReadMotion();
        }

        void DisposeVolume()
        {
            foreach (var d in new IDisposable[] { volume, depthFloat, pointCloud, colourInput, shadedSurface, shadedColour, relocator }) d?.Dispose();
            relocator = null;
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
    }
}
