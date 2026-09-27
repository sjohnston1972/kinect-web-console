using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KinectBridge.Sensor;
using KinectBridge.Skeleton;
using Microsoft.Kinect.Toolkit.Fusion;

namespace KinectBridge.Fusion
{
    /// <summary>
    /// Writes a Kinect Fusion mesh as STL, OBJ or PLY, placed so it opens the right way up:
    /// 1. Unmirror: the Kinect's depth picture is a mirror image, so the raw model is too; flip it back.
    /// 2. Level: turn it so the floor is flat, using the accelerometer reading from when the scan began.
    /// 3. Clean up, if asked: remove small floating pieces, and the floor.
    /// 4. Stand it on the ground at the middle of the scene, facing the front, and in each program's usual units
    ///    and axes: STL in millimetres with Z up (Windows 3D Viewer, 3D printing); OBJ in metres with Y up and
    ///    PLY in metres with Z up (both import upright in Blender with default settings).
    /// </summary>
    static class MeshWriter
    {
        const double PieceShare = 0.02;        // pieces with under 2% of the biggest piece's triangles are dropped
        const int SmallestKeptPiece = 50;      // and always pieces of fewer than 50 triangles
        const double FlatEnough = 0.9;         // a triangle facing this nearly straight up counts as floor
        const double FloorBand = 0.03;         // metres either side of the floor's height
        const double FloorBin = 0.01;          // floor height is found to the centimetre

        public class Cleanup
        {
            public bool RemoveFragments = true;
            public bool RemoveFloor;
        }

        public class Result
        {
            public int Triangles;
            public int Vertices;
            public double SizeX, SizeY, SizeZ;   // metres: width, depth, height
            public int RemovedPieces, RemovedPieceTriangles, RemovedFloorTriangles;
        }

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static Result Write(ColorMesh mesh, string format, string path, bool withColour, MotionReading gravity, Cleanup cleanup)
        {
            var source = mesh.GetVertices();
            var indexes = mesh.GetTriangleIndexes();
            var colours = withColour ? mesh.GetColors() : null;

            // Fusion's camera space: X right, Y down, Z away from the Kinect.
            // Gravity from the accelerometer is given with Y up, so flip its Y to match.
            var level = Quat.Identity;
            if (gravity != null)
                level = Quat.Between(new Vec3(gravity.X, -gravity.Y, gravity.Z), new Vec3(0, 1, 0));

            var zUp = format != "obj";
            var points = new Vec3[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                var v = source[i];
                var p = level.Rotate(new Vec3(-v.X, v.Y, v.Z));      // unmirror, then level (still Y down)
                p = new Vec3(p.X, -p.Y, -p.Z);                        // Y up, facing the viewer: half a turn about X
                points[i] = zUp ? new Vec3(p.X, -p.Z, p.Y) : p;       // Z up for STL and PLY
            }

            // Unmirroring turned every triangle inside out, so list each one's corners the other way round
            var triangles = new List<int>(indexes.Count);
            for (int t = 0; t + 2 < indexes.Count; t += 3)
            {
                triangles.Add(indexes[t]);
                triangles.Add(indexes[t + 2]);
                triangles.Add(indexes[t + 1]);
            }

            // Fusion lists three separate corners for every triangle; find which are really the same point
            var shared = Weld(points);
            var result = new Result();
            if (cleanup.RemoveFloor) triangles = RemoveFloor(points, triangles, zUp, out result.RemovedFloorTriangles);
            if (cleanup.RemoveFragments) triangles = RemoveFragments(triangles, shared, out result.RemovedPieces, out result.RemovedPieceTriangles);
            if (triangles.Count == 0) throw new InvalidOperationException("Nothing is left after cleaning up. Try exporting without removing the floor.");

            PlaceOnGround(points, triangles, zUp, result);
            result.Triangles = triangles.Count / 3;

            switch (format)
            {
                case "stl": WriteStl(path, points, triangles); result.Vertices = triangles.Count; break;
                case "obj": result.Vertices = WriteObj(path, points, triangles, shared, colours); break;
                case "ply": result.Vertices = WritePly(path, points, triangles, shared, colours); break;
                default: throw new ArgumentException("Unknown format " + format);
            }
            return result;
        }

        /// <summary>For each vertex, a number shared by every vertex at exactly the same spot.</summary>
        static int[] Weld(Vec3[] points)
        {
            var map = new Dictionary<(float, float, float), int>();
            var shared = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var key = ((float)points[i].X, (float)points[i].Y, (float)points[i].Z);
                if (!map.TryGetValue(key, out var index)) map[key] = index = map.Count;
                shared[i] = index;
            }
            return shared;
        }

        static double Up(Vec3 p, bool zUp) => zUp ? p.Z : p.Y;

        /// <summary>
        /// Finds the floor as the lowest height where many flat, upward-facing triangles sit, and removes the
        /// flat triangles within 3 cm of it. Works best when the floor is level (the export levels it first).
        /// </summary>
        static List<int> RemoveFloor(Vec3[] points, List<int> triangles, bool zUp, out int removed)
        {
            removed = 0;
            var flat = new List<int>();          // triangle starts that face straight up or down
            for (int t = 0; t < triangles.Count; t += 3)
            {
                Vec3 a = points[triangles[t]], b = points[triangles[t + 1]], c = points[triangles[t + 2]];
                var n = Vec3.Cross(b - a, c - a).Normalised;
                if (Math.Abs(Up(n, zUp)) >= FlatEnough) flat.Add(t);
            }
            if (flat.Count < 100) return triangles;

            // A histogram of flat triangles by height, a centimetre per bin; the floor is the lowest busy bin
            var bins = new SortedDictionary<int, int>();
            foreach (var t in flat)
            {
                var h = (Up(points[triangles[t]], zUp) + Up(points[triangles[t + 1]], zUp) + Up(points[triangles[t + 2]], zUp)) / 3;
                var bin = (int)Math.Floor(h / FloorBin);
                bins[bin] = bins.TryGetValue(bin, out var n) ? n + 1 : 1;
            }
            var busy = Math.Max(100, flat.Count * 0.03);
            var floorBin = bins.Where(kv => kv.Value >= busy).Select(kv => (int?)kv.Key).FirstOrDefault();
            if (floorBin == null) return triangles;
            var floor = (floorBin.Value + 0.5) * FloorBin;

            var drop = new HashSet<int>();
            foreach (var t in flat)
            {
                bool Near(int i) => Math.Abs(Up(points[triangles[i]], zUp) - floor) <= FloorBand;
                if (Near(t) && Near(t + 1) && Near(t + 2)) drop.Add(t);
            }
            removed = drop.Count;
            var kept = new List<int>(triangles.Count - drop.Count * 3);
            for (int t = 0; t < triangles.Count; t += 3)
                if (!drop.Contains(t)) { kept.Add(triangles[t]); kept.Add(triangles[t + 1]); kept.Add(triangles[t + 2]); }
            return kept;
        }

        /// <summary>
        /// Splits the mesh into connected pieces (triangles that share corners) and drops the small ones:
        /// specks of noise, and ghosts of things that moved during the scan.
        /// </summary>
        static List<int> RemoveFragments(List<int> triangles, int[] shared, out int pieces, out int removed)
        {
            // Union-find over the shared corner numbers: corners of one triangle join the same piece
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
                while (p != x) { var up = parent[p]; parent[x] = up; x = p; p = up; }
                return x;
            }
            for (int t = 0; t < triangles.Count; t += 3)
            {
                int a = Find(shared[triangles[t]]), b = Find(shared[triangles[t + 1]]), c = Find(shared[triangles[t + 2]]);
                parent[b] = a;
                parent[Find(c)] = a;
            }

            var size = new Dictionary<int, int>();
            for (int t = 0; t < triangles.Count; t += 3)
            {
                var piece = Find(shared[triangles[t]]);
                size[piece] = size.TryGetValue(piece, out var n) ? n + 1 : 1;
            }
            var keepFrom = Math.Max(SmallestKeptPiece, (size.Count > 0 ? size.Values.Max() : 0) * PieceShare);
            var small = new HashSet<int>(size.Where(kv => kv.Value < keepFrom).Select(kv => kv.Key));
            pieces = small.Count;
            removed = small.Sum(p => size[p]);

            var kept = new List<int>(triangles.Count);
            for (int t = 0; t < triangles.Count; t += 3)
                if (!small.Contains(Find(shared[triangles[t]])))
                {
                    kept.Add(triangles[t]); kept.Add(triangles[t + 1]); kept.Add(triangles[t + 2]);
                }
            return kept;
        }

        /// <summary>Moves the model so its lowest point is on the ground and it is centred across the other two axes.</summary>
        static void PlaceOnGround(Vec3[] points, List<int> triangles, bool zUp, Result result)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var used = new HashSet<int>(triangles);
            foreach (var i in used)
            {
                var p = points[i];
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            var shift = zUp
                ? new Vec3(-(minX + maxX) / 2, -(minY + maxY) / 2, -minZ)
                : new Vec3(-(minX + maxX) / 2, -minY, -(minZ + maxZ) / 2);
            foreach (var i in used) points[i] = points[i] + shift;

            result.SizeX = maxX - minX;
            result.SizeY = zUp ? maxY - minY : maxZ - minZ;
            result.SizeZ = zUp ? maxZ - minZ : maxY - minY;
        }

        /// <summary>Binary STL, in millimetres. Each triangle carries its own corners and a facing direction.</summary>
        static void WriteStl(string path, Vec3[] points, List<int> triangles)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                var header = new byte[80];
                Encoding.ASCII.GetBytes("Kinect Web Console scan, millimetres, Z up").CopyTo(header, 0);
                w.Write(header);
                w.Write((uint)(triangles.Count / 3));
                for (int t = 0; t < triangles.Count; t += 3)
                {
                    Vec3 a = points[triangles[t]] * 1000, b = points[triangles[t + 1]] * 1000, c = points[triangles[t + 2]] * 1000;
                    var n = Vec3.Cross(b - a, c - a).Normalised;
                    foreach (var v in new[] { n, a, b, c }) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }
                    w.Write((ushort)0);
                }
            }
        }

        /// <summary>
        /// The corners actually used, one per shared spot, and each triangle corner's number in that list.
        /// OBJ and PLY share corners between triangles, which makes the files a third of the size and lets
        /// Blender smooth the surface.
        /// </summary>
        static List<int> Compact(List<int> triangles, int[] shared, out int[] corner)
        {
            var number = new Dictionary<int, int>();
            var vertices = new List<int>();
            corner = new int[triangles.Count];
            for (int k = 0; k < triangles.Count; k++)
            {
                var s = shared[triangles[k]];
                if (!number.TryGetValue(s, out var n))
                {
                    n = vertices.Count;
                    number[s] = n;
                    vertices.Add(triangles[k]);
                }
                corner[k] = n;
            }
            return vertices;
        }

        /// <summary>Colour as red, green, blue from 0 to 255. Fusion packs colours as blue, green, red, alpha bytes.</summary>
        static void Rgb(int packed, out byte r, out byte g, out byte b)
        {
            r = (byte)((packed >> 16) & 0xFF);
            g = (byte)((packed >> 8) & 0xFF);
            b = (byte)(packed & 0xFF);
        }

        /// <summary>Text OBJ, in metres, Y up. With colour, each vertex line carries red, green and blue from 0 to 1.</summary>
        static int WriteObj(string path, Vec3[] points, List<int> triangles, int[] shared, IList<int> colours)
        {
            var vertices = Compact(triangles, shared, out var corner);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20))
            {
                w.WriteLine("# Kinect Web Console scan, metres, Y up");
                foreach (var i in vertices)
                {
                    var p = points[i];
                    w.Write("v {0} {1} {2}", F(p.X), F(p.Y), F(p.Z));
                    if (colours != null)
                    {
                        Rgb(colours[i], out var r, out var g, out var b);
                        w.Write(" {0} {1} {2}", (r / 255.0).ToString("0.###", Invariant), (g / 255.0).ToString("0.###", Invariant), (b / 255.0).ToString("0.###", Invariant));
                    }
                    w.WriteLine();
                }
                for (int k = 0; k < corner.Length; k += 3)
                    w.WriteLine("f {0} {1} {2}", corner[k] + 1, corner[k + 1] + 1, corner[k + 2] + 1);
            }
            return vertices.Count;
        }

        /// <summary>Binary PLY, in metres, Z up, with vertex colours when colour capture was on.</summary>
        static int WritePly(string path, Vec3[] points, List<int> triangles, int[] shared, IList<int> colours)
        {
            var vertices = Compact(triangles, shared, out var corner);
            using (var stream = File.Create(path))
            {
                var header = new StringBuilder();
                header.Append("ply\nformat binary_little_endian 1.0\ncomment Kinect Web Console scan, metres, Z up\n");
                header.Append($"element vertex {vertices.Count}\nproperty float x\nproperty float y\nproperty float z\n");
                if (colours != null) header.Append("property uchar red\nproperty uchar green\nproperty uchar blue\n");
                header.Append($"element face {corner.Length / 3}\nproperty list uchar int vertex_indices\nend_header\n");
                var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);

                using (var w = new BinaryWriter(stream))
                {
                    foreach (var i in vertices)
                    {
                        w.Write((float)points[i].X); w.Write((float)points[i].Y); w.Write((float)points[i].Z);
                        if (colours != null)
                        {
                            Rgb(colours[i], out var r, out var g, out var b);
                            w.Write(r); w.Write(g); w.Write(b);
                        }
                    }
                    for (int k = 0; k < corner.Length; k += 3)
                    {
                        w.Write((byte)3);
                        w.Write(corner[k]); w.Write(corner[k + 1]); w.Write(corner[k + 2]);
                    }
                }
            }
            return vertices.Count;
        }

        static string F(double v) => v.ToString("0.#####", Invariant);
    }
}
