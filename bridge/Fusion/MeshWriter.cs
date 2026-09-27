using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    /// 3. Stand it on the ground at the middle of the scene, facing the front, and in each program's usual units
    ///    and axes: STL in millimetres with Z up (Windows 3D Viewer, 3D printing); OBJ in metres with Y up and
    ///    PLY in metres with Z up (both import upright in Blender with default settings).
    /// </summary>
    static class MeshWriter
    {
        public class Result
        {
            public int Triangles;
            public int Vertices;
            public double SizeX, SizeY, SizeZ;   // metres: width, depth, height
        }

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static Result Write(ColorMesh mesh, string format, string path, bool withColour, MotionReading gravity)
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
            var result = PlaceOnGround(points, zUp);

            // Unmirroring turned every triangle inside out, so list each one's corners the other way round
            var triangles = new int[indexes.Count];
            for (int t = 0; t + 2 < indexes.Count; t += 3)
            {
                triangles[t] = indexes[t];
                triangles[t + 1] = indexes[t + 2];
                triangles[t + 2] = indexes[t + 1];
            }
            result.Triangles = triangles.Length / 3;

            switch (format)
            {
                case "stl": WriteStl(path, points, triangles); result.Vertices = points.Length; break;
                case "obj": result.Vertices = WriteObj(path, points, triangles, colours); break;
                case "ply": result.Vertices = WritePly(path, points, triangles, colours); break;
                default: throw new ArgumentException("Unknown format " + format);
            }
            return result;
        }

        /// <summary>Moves the model so its lowest point is on the ground and it is centred across the other two axes.</summary>
        static Result PlaceOnGround(Vec3[] points, bool zUp)
        {
            if (points.Length == 0) return new Result();
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            var shift = zUp
                ? new Vec3(-(minX + maxX) / 2, -(minY + maxY) / 2, -minZ)
                : new Vec3(-(minX + maxX) / 2, -minY, -(minZ + maxZ) / 2);
            for (int i = 0; i < points.Length; i++) points[i] = points[i] + shift;

            return zUp
                ? new Result { SizeX = maxX - minX, SizeY = maxY - minY, SizeZ = maxZ - minZ }
                : new Result { SizeX = maxX - minX, SizeY = maxZ - minZ, SizeZ = maxY - minY };
        }

        /// <summary>Binary STL, in millimetres. Each triangle carries its own corners and a facing direction.</summary>
        static void WriteStl(string path, Vec3[] points, int[] triangles)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                var header = new byte[80];
                Encoding.ASCII.GetBytes("Kinect Web Console scan, millimetres, Z up").CopyTo(header, 0);
                w.Write(header);
                w.Write((uint)(triangles.Length / 3));
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    Vec3 a = points[triangles[t]] * 1000, b = points[triangles[t + 1]] * 1000, c = points[triangles[t + 2]] * 1000;
                    var n = Vec3.Cross(b - a, c - a).Normalised;
                    foreach (var v in new[] { n, a, b, c }) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }
                    w.Write((ushort)0);
                }
            }
        }

        /// <summary>
        /// Fusion lists three separate corners for every triangle. OBJ and PLY share corners between triangles,
        /// which makes the files a third of the size and lets Blender smooth the surface.
        /// </summary>
        static List<int> Weld(Vec3[] points, out List<int> firstSource)
        {
            var map = new Dictionary<(float, float, float), int>();
            var remap = new List<int>(points.Length);
            firstSource = new List<int>();
            for (int i = 0; i < points.Length; i++)
            {
                var key = ((float)points[i].X, (float)points[i].Y, (float)points[i].Z);
                if (!map.TryGetValue(key, out var index))
                {
                    index = firstSource.Count;
                    map[key] = index;
                    firstSource.Add(i);
                }
                remap.Add(index);
            }
            return remap;
        }

        /// <summary>Colour as red, green, blue from 0 to 255. Fusion packs colours as blue, green, red, alpha bytes.</summary>
        static void Rgb(int packed, out byte r, out byte g, out byte b)
        {
            r = (byte)((packed >> 16) & 0xFF);
            g = (byte)((packed >> 8) & 0xFF);
            b = (byte)(packed & 0xFF);
        }

        /// <summary>Text OBJ, in metres, Y up. With colour, each vertex line carries red, green and blue from 0 to 1.</summary>
        static int WriteObj(string path, Vec3[] points, int[] triangles, IList<int> colours)
        {
            var remap = Weld(points, out var unique);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20))
            {
                w.WriteLine("# Kinect Web Console scan, metres, Y up");
                foreach (var i in unique)
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
                for (int t = 0; t < triangles.Length; t += 3)
                    w.WriteLine("f {0} {1} {2}", remap[triangles[t]] + 1, remap[triangles[t + 1]] + 1, remap[triangles[t + 2]] + 1);
            }
            return unique.Count;
        }

        /// <summary>Binary PLY, in metres, Z up, with vertex colours when colour capture was on.</summary>
        static int WritePly(string path, Vec3[] points, int[] triangles, IList<int> colours)
        {
            var remap = Weld(points, out var unique);
            using (var stream = File.Create(path))
            {
                var header = new StringBuilder();
                header.Append("ply\nformat binary_little_endian 1.0\ncomment Kinect Web Console scan, metres, Z up\n");
                header.Append($"element vertex {unique.Count}\nproperty float x\nproperty float y\nproperty float z\n");
                if (colours != null) header.Append("property uchar red\nproperty uchar green\nproperty uchar blue\n");
                header.Append($"element face {triangles.Length / 3}\nproperty list uchar int vertex_indices\nend_header\n");
                var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);

                using (var w = new BinaryWriter(stream))
                {
                    foreach (var i in unique)
                    {
                        w.Write((float)points[i].X); w.Write((float)points[i].Y); w.Write((float)points[i].Z);
                        if (colours != null)
                        {
                            Rgb(colours[i], out var r, out var g, out var b);
                            w.Write(r); w.Write(g); w.Write(b);
                        }
                    }
                    for (int t = 0; t < triangles.Length; t += 3)
                    {
                        w.Write((byte)3);
                        w.Write(remap[triangles[t]]); w.Write(remap[triangles[t + 1]]); w.Write(remap[triangles[t + 2]]);
                    }
                }
            }
            return unique.Count;
        }

        static string F(double v) => v.ToString("0.#####", Invariant);
    }
}
