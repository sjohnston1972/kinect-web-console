using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KinectBridge.Skeleton;

namespace KinectBridge.Mocap
{
    /// <summary>
    /// Turns a take into a BVH file, the motion capture format Blender imports (File, Import, Motion Capture).
    ///
    /// How the Kinect skeleton maps onto BVH:
    /// - The root, "Hips", sits at the hip centre and moves with it. Its rotation is the SDK's rotation for the hips.
    /// - Every other BVH joint is one Kinect bone (for example "UpperArmLeft" runs shoulder to elbow), starting
    ///   where its parent bone ends. Each bone points along its own +Y, the SDK's convention, so its rotation
    ///   is exactly the SDK's hierarchical bone orientation for that bone.
    /// - Bone lengths are the typical distances between the joints over the whole take.
    /// Units are metres, in the Kinect's own axes: Y up, and the person facing -Z (towards the Kinect).
    /// Frames are evened out to exactly 30 a second.
    /// </summary>
    static class BvhExporter
    {
        const double FramesPerSecond = 30;

        // BVH name for the bone ending at each Kinect joint (index = joint number); the hip centre is the root
        static readonly string[] BoneNames =
        {
            "Hips", "LowerSpine", "UpperSpine", "Neck",
            "CollarLeft", "UpperArmLeft", "ForearmLeft", "HandLeft",
            "CollarRight", "UpperArmRight", "ForearmRight", "HandRight",
            "PelvisLeft", "ThighLeft", "ShinLeft", "FootLeft",
            "PelvisRight", "ThighRight", "ShinRight", "FootRight",
        };

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public class Result
        {
            public int Frames;
            public int PersonId;
            public int PeopleInTake;
            public double AverageErrorCm;   // how far the BVH's joints land from the recorded ones
        }

        class Pose
        {
            public double T;   // seconds
            public readonly Vec3?[] Position = new Vec3?[Joints.Count];
            public readonly Quat?[] Rotation = new Quat?[Joints.Count];
        }

        public static Result Export(Dictionary<string, object> take, string path)
        {
            if (take["mode"] as string == "seated")
                throw new InvalidOperationException("BVH needs a take recorded in Standing mode. Seated mode has no hips or legs to build the skeleton from.");

            var poses = ReadPoses(take, out var personId, out var people);

            // Only the trimmed part of the take, if it has been trimmed
            if (take.TryGetValue("trim", out var t) && t is Dictionary<string, object> trim)
            {
                var start = Convert.ToDouble(trim["start"]);
                var end = Convert.ToDouble(trim["end"]);
                poses = poses.Where(p => p.T >= start && p.T <= end).ToList();
            }
            if (poses.Count(p => p.Position[Joints.HipCenter] != null) < 2)
                throw new InvalidOperationException("The take has too little full-body tracking to export. Record again standing with your whole body in view.");

            var lengths = BoneLengths(poses);
            var order = DepthFirstOrder();
            var frames = Resample(poses);
            PlaceOnFloor(frames, take.TryGetValue("floor", out var f) ? f as object[] : null);

            var bvh = new StringBuilder();
            bvh.AppendLine("HIERARCHY");
            WriteHierarchy(bvh, lengths);
            bvh.AppendLine("MOTION");
            bvh.AppendLine("Frames: " + frames.Count);
            bvh.AppendLine("Frame Time: " + (1 / FramesPerSecond).ToString("0.0000000", Invariant));

            double errorSum = 0;
            int errorCount = 0;
            foreach (var pose in frames)
            {
                var hip = pose.Position[Joints.HipCenter].Value;
                var values = new List<double> { hip.X, hip.Y, hip.Z };
                var usedRotations = new Quat[Joints.Count];
                foreach (var j in order)
                {
                    var q = (pose.Rotation[j] ?? Quat.Identity).Normalised;
                    q.ToEulerZXY(out var z, out var x, out var y);
                    values.Add(z); values.Add(x); values.Add(y);
                    usedRotations[j] = Quat.FromEulerZXY(z, x, y);   // check the angles, not just the quaternions
                }
                bvh.AppendLine(string.Join(" ", values.Select(v => v.ToString("0.#####", Invariant))));
                Measure(pose, usedRotations, lengths, ref errorSum, ref errorCount);
            }

            File.WriteAllText(path, bvh.ToString(), new UTF8Encoding(false));
            return new Result
            {
                Frames = frames.Count,
                PersonId = personId,
                PeopleInTake = people,
                AverageErrorCm = errorCount > 0 ? Math.Round(errorSum / errorCount * 100, 2) : 0,
            };
        }

        /// <summary>The frames for one person: the one tracked in the most frames.</summary>
        static List<Pose> ReadPoses(Dictionary<string, object> take, out int personId, out int people)
        {
            var frames = ((object[])take["frames"]).Cast<Dictionary<string, object>>().ToList();
            var counts = new Dictionary<int, int>();
            foreach (var frame in frames)
                foreach (Dictionary<string, object> body in (object[])frame["bodies"])
                {
                    var id = Convert.ToInt32(body["id"]);
                    counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
                }
            people = counts.Count;
            if (counts.Count == 0) throw new InvalidOperationException("Nobody was tracked in this take.");
            var chosen = counts.OrderByDescending(kv => kv.Value).First().Key;
            personId = chosen;

            var poses = new List<Pose>();
            foreach (var frame in frames)
            {
                var body = ((object[])frame["bodies"]).Cast<Dictionary<string, object>>()
                    .FirstOrDefault(b => Convert.ToInt32(b["id"]) == chosen);
                if (body == null) continue;
                var pose = new Pose { T = Convert.ToDouble(frame["t"]) / 1000 };
                var joints = (Dictionary<string, object>)body["joints"];
                for (int j = 0; j < Joints.Count; j++)
                {
                    if (!joints.TryGetValue(Joints.Names[j], out var raw)) continue;
                    var joint = (Dictionary<string, object>)raw;
                    var p = Numbers(joint["p"]);
                    pose.Position[j] = new Vec3(p[0], p[1], p[2]);
                    if (joint.TryGetValue("rot", out var r))
                    {
                        var q = Numbers(r);
                        pose.Rotation[j] = new Quat(q[0], q[1], q[2], q[3]);
                    }
                }
                poses.Add(pose);
            }
            return poses;
        }

        static double[] Numbers(object array) => ((object[])array).Select(Convert.ToDouble).ToArray();

        /// <summary>The middle value of each bone's length over the take, which ignores the odd bad frame.</summary>
        static double[] BoneLengths(List<Pose> poses)
        {
            var lengths = new double[Joints.Count];
            for (int j = 1; j < Joints.Count; j++)
            {
                var parent = Joints.Parent[j];
                var samples = poses.Where(p => p.Position[j] != null && p.Position[parent] != null)
                    .Select(p => (p.Position[j].Value - p.Position[parent].Value).Length).OrderBy(l => l).ToList();
                lengths[j] = samples.Count > 0 ? samples[samples.Count / 2] : 0.1;
            }
            return lengths;
        }

        /// <summary>Joints in the order BVH lists them: each bone, then everything hanging from it.</summary>
        static List<int> DepthFirstOrder()
        {
            var order = new List<int>();
            void Visit(int j)
            {
                order.Add(j);
                for (int c = 0; c < Joints.Count; c++) if (Joints.Parent[c] == j) Visit(c);
            }
            Visit(Joints.HipCenter);
            return order;
        }

        static void WriteHierarchy(StringBuilder bvh, double[] lengths)
        {
            void Bone(int j, int depth)
            {
                var pad = new string('\t', depth);
                var parent = Joints.Parent[j];
                bvh.AppendLine(pad + (parent < 0 ? "ROOT " : "JOINT ") + BoneNames[j]);
                bvh.AppendLine(pad + "{");
                // A bone starts where its parent bone ends: along the parent's +Y. Bones leaving the hips start at the hips.
                var offset = parent <= Joints.HipCenter ? 0 : lengths[parent];
                bvh.AppendLine($"{pad}\tOFFSET 0 {offset.ToString("0.#####", Invariant)} 0");
                bvh.AppendLine(pad + (parent < 0
                    ? "\tCHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation"
                    : "\tCHANNELS 3 Zrotation Xrotation Yrotation"));

                var children = Enumerable.Range(0, Joints.Count).Where(c => Joints.Parent[c] == j).ToList();
                foreach (var c in children) Bone(c, depth + 1);
                if (children.Count == 0)
                {
                    bvh.AppendLine(pad + "\tEnd Site");
                    bvh.AppendLine(pad + "\t{");
                    bvh.AppendLine($"{pad}\t\tOFFSET 0 {lengths[j].ToString("0.#####", Invariant)} 0");
                    bvh.AppendLine(pad + "\t}");
                }
                bvh.AppendLine(pad + "}");
            }
            Bone(Joints.HipCenter, 0);
        }

        /// <summary>
        /// Evens the frames out to exactly 30 a second by taking the nearest recorded frame for each moment.
        /// Joints missing from a frame keep their last known rotation, so the skeleton never snaps to a default pose.
        /// </summary>
        static List<Pose> Resample(List<Pose> poses)
        {
            poses = poses.Where(p => p.Position[Joints.HipCenter] != null).ToList();
            var start = poses[0].T;
            var end = poses[poses.Count - 1].T;
            var result = new List<Pose>();
            var lastRotation = new Quat?[Joints.Count];
            int i = 0;
            for (double t = start; t <= end + 1e-6; t += 1 / FramesPerSecond)
            {
                while (i + 1 < poses.Count && Math.Abs(poses[i + 1].T - t) <= Math.Abs(poses[i].T - t)) i++;
                var source = poses[i];
                var pose = new Pose { T = t };
                for (int j = 0; j < Joints.Count; j++)
                {
                    pose.Position[j] = source.Position[j];
                    pose.Rotation[j] = source.Rotation[j] ?? lastRotation[j];
                    if (source.Rotation[j] != null) lastRotation[j] = source.Rotation[j];
                }
                result.Add(pose);
            }
            return result;
        }

        /// <summary>
        /// Moves the whole take so it is easy to work with in Blender, without changing the motion itself:
        /// 1. Level: the Kinect is usually tilted, which tilts everything it records. The floor it detected
        ///    tells us by how much, so the take is turned until that floor is flat.
        /// 2. Stand on the floor: the take is raised so the floor is at height 0 (the Kinect records from its own height).
        /// 3. Face the front: the take is turned half a circle, so the person faces Blender's front view
        ///    rather than showing their back.
        /// 4. Centre: the hips' usual position over the take is above the middle of the scene.
        /// Only the root (the hips) is moved and turned; every other bone hangs from it, so it all follows.
        /// </summary>
        static void PlaceOnFloor(List<Pose> frames, object[] floor)
        {
            var level = Quat.Identity;
            double floorHeight;
            if (floor != null && floor.Length == 4 && Convert.ToDouble(floor[1]) > 0.5)
            {
                var normal = new Vec3(Convert.ToDouble(floor[0]), Convert.ToDouble(floor[1]), Convert.ToDouble(floor[2]));
                var size = normal.Length;
                level = Quat.Between(normal, Vec3.Up);
                floorHeight = -Convert.ToDouble(floor[3]) / size;   // after levelling, the floor sits at this height
            }
            else
            {
                // No floor seen: use the lowest foot in the take instead
                floorHeight = frames.SelectMany(p => new[] { p.Position[Joints.FootLeft], p.Position[Joints.FootRight] })
                    .Where(v => v != null).Select(v => v.Value.Y).DefaultIfEmpty(-1).Min();
            }

            var turn = new Quat(0, 1, 0, 0);   // half a circle about the vertical
            var whole = turn * level;
            // Centre on where the hips usually are, not the first frame, which is often mid-walk into position
            var hips = frames.Select(p => whole.Rotate(p.Position[Joints.HipCenter].Value)).ToList();
            double Middle(IEnumerable<double> values) { var s = values.OrderBy(v => v).ToList(); return s[s.Count / 2]; }
            var shift = new Vec3(-Middle(hips.Select(h => h.X)), -floorHeight, -Middle(hips.Select(h => h.Z)));

            foreach (var pose in frames)
            {
                for (int j = 0; j < Joints.Count; j++)
                    if (pose.Position[j] != null) pose.Position[j] = whole.Rotate(pose.Position[j].Value) + shift;
                if (pose.Rotation[Joints.HipCenter] != null)
                    pose.Rotation[Joints.HipCenter] = whole * pose.Rotation[Joints.HipCenter].Value;
            }
        }

        /// <summary>
        /// Rebuilds the joints the way Blender will (from the hips, bone by bone) and adds up how far
        /// each lands from where the Kinect recorded it. A small number means the BVH moves like the person did.
        /// </summary>
        static void Measure(Pose pose, Quat[] rotation, double[] lengths, ref double sum, ref int count)
        {
            var absolute = new Quat[Joints.Count];
            var position = new Vec3[Joints.Count];
            absolute[Joints.HipCenter] = rotation[Joints.HipCenter];
            position[Joints.HipCenter] = pose.Position[Joints.HipCenter].Value;
            for (int j = 1; j < Joints.Count; j++)
            {
                var parent = Joints.Parent[j];
                absolute[j] = absolute[parent] * rotation[j];
                position[j] = position[parent] + absolute[j].Rotate(new Vec3(0, lengths[j], 0));
                if (pose.Position[j] == null) continue;
                sum += (position[j] - pose.Position[j].Value).Length;
                count++;
            }
        }
    }
}
