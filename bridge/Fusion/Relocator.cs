using System;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit.Fusion;

namespace KinectBridge.Fusion
{
    /// <summary>
    /// Finds the Kinect's place again after 3D scan tracking is lost, using Kinect Fusion's camera pose finder.
    ///
    /// While tracking is good, it keeps a small library of what the scene looked like from where the Kinect was
    /// (depth and colour features, with the position). When tracking is lost, it looks up the views most like
    /// the current one, and tries lining the depth picture up against the model from each of those positions.
    /// If one fits, scanning carries on from there, with no Reset.
    /// </summary>
    class Relocator : IDisposable
    {
        const int RememberEvery = 5;           // tracked frames between remembered views
        const float RejectDistance = 1.0f;     // views less alike than this are not worth trying (the SDK sample's value)
        const int PosesToTry = 5;
        const float GoodFit = 0.3f;            // alignment energy below this counts as a fit (lower is better)

        readonly CameraPoseFinder finder;
        readonly FusionFloatImageFrame delta;
        int sinceRemembered;

        public Relocator(int width, int height)
        {
            finder = CameraPoseFinder.FusionCreateCameraPoseFinder(CameraPoseFinderParameters.Defaults);
            delta = new FusionFloatImageFrame(width, height);
        }

        /// <summary>True when it is time to remember the current view (every few tracked frames).</summary>
        public bool Due => sinceRemembered + 1 >= RememberEvery;

        /// <summary>Called after each tracked frame. Stores the view when it is due.</summary>
        public void Tracked(FusionFloatImageFrame depth, FusionColorImageFrame colour, Matrix4 worldToCamera)
        {
            if (++sinceRemembered < RememberEvery || colour == null) return;
            sinceRemembered = 0;
            finder.ProcessFrame(depth, colour, worldToCamera, CameraPoseFinder.DefaultMinimumDistanceThreshold, out _, out _);
        }

        /// <summary>Tries to find where the Kinect is now. On success, pose is its new position.</summary>
        public bool TryRecover(ColorReconstruction volume, FusionFloatImageFrame depth, FusionColorImageFrame colour, out Matrix4 pose)
        {
            pose = Matrix4.Identity;
            if (finder.GetStoredPoseCount() == 0) return false;
            using (var matches = finder.FindCameraPose(depth, colour))
            {
                if (matches == null || matches.GetPoseCount() == 0 || matches.CalculateMinimumDistance() >= RejectDistance) return false;
                var candidates = matches.GetMatchPoses();
                for (int n = 0; n < Math.Min(candidates.Count, PosesToTry); n++)
                {
                    var fits = volume.AlignDepthFloatToReconstruction(depth, FusionDepthProcessor.DefaultAlignIterationCount, delta, out var energy, candidates[n]);
                    if (fits && energy < GoodFit)
                    {
                        pose = volume.GetCurrentWorldToCameraTransform();
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Forgets every remembered view (a new scan).</summary>
        public void Reset()
        {
            finder.ResetCameraPoseFinder();
            sinceRemembered = 0;
        }

        public void Dispose()
        {
            finder.Dispose();
            delta.Dispose();
        }
    }
}
