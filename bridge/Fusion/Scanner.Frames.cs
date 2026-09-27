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
    /// <summary>3D scanning, part: each depth frame (filtering, lining up and merging it, recovering lost tracking, and the preview picture).</summary>
    partial class Scanner
    {
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
                    var keep = filter;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        short mm = depth.Depth[i];
                        if (keep != null && !keep.Keep(i % Width, i / Width, mm)) mm = 0;   // turntable mode hides the rest
                        pixels[i] = new DepthImagePixel { Depth = mm, PlayerIndex = depth.Player[i] };
                    }
                    volume.DepthToDepthFloatFrame(pixels, depthFloat, volumePreset.MinDepth, volumePreset.MaxDepth, false);

                    bool tracked;
                    var colourReady = colour && PrepareColour(depth);
                    try
                    {
                        tracked = colourReady
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
                        // Remember this view every few frames, so a lost scan can find its place again
                        if (relocator != null && relocator.Due && (colourReady || PrepareColour(depth)))
                            relocator.Tracked(depthFloat, colourInput, worldToCamera);
                        else relocator?.Tracked(depthFloat, null, worldToCamera);
                    }
                    else
                    {
                        if (++lostFrames == LostAfterFrames) Log.Warn("3D scan: tracking lost");
                        // While lost, look up the remembered views every few frames and try the likeliest positions
                        if (lostFrames >= LostAfterFrames && lostFrames % 3 == 0 && relocator != null
                            && (colourReady || PrepareColour(depth))
                            && relocator.TryRecover(volume, depthFloat, colourInput, out var found))
                        {
                            worldToCamera = found;
                            lostFrames = 0;
                            recovered++;
                            Log.Info("3D scan: tracking found again by recognising the view");
                        }
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
    }
}
