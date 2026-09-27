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
    /// <summary>3D scanning, part: saving the model as STL, OBJ or PLY.</summary>
    partial class Scanner
    {
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
    }
}
