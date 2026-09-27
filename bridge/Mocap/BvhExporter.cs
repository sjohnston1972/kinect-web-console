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
    /// - The root, "Hips", sits at the hip centre and moves with it.
    /// - Every other BVH joint is one Kinect bone (for example "UpperArmLeft" runs shoulder to elbow), starting
    ///   where its parent bone ends. Bone lengths are the typical distances between the joints over the take.
    /// - The rest pose (the skeleton with every rotation at zero) is a T-pose facing +Z: arms straight out,
    ///   legs straight down, and the spine, collar bones, hips and feet in the person's own typical shape.
    ///   That is what retargeting tools expect when putting the motion onto another character.
    /// - Each frame's rotations are the SDK's bone orientations, converted to turn that T-pose into the
    ///   recorded pose: a bone's rotation = its SDK orientation x the fixed turn from its T-pose direction to the
    ///   SDK's "bones point along +Y" convention, relative to its parent bone's.
    /// Units are metres, Y up. The take is levelled, stood on the floor, centred and turned to face Blender's
    /// front view (PlaceOnFloor), and frames where a foot would sink below the floor are lifted.
    /// Frames are evened out to exactly 30 a second.
    /// </summary>
    static class BvhExporter
    {
        const double FramesPerSecond = 30;
        const double RootNubMetres = 0.004;   // a small offset so Blender does not see a zero-length Hips bone (it averages the 3 children: 1.3 mm, over its 1 mm limit)

        // BVH name for the bone ending at each Kinect joint (index = joint number); the hip centre is the root
        static readonly string[] BoneNames =
        {
            "Hips", "LowerSpine", "UpperSpine", "Neck",
            "CollarLeft", "UpperArmLeft", "ForearmLeft", "HandLeft",
            "CollarRight", "UpperArmRight", "ForearmRight", "HandRight",
            "PelvisLeft", "ThighLeft", "ShinLeft", "FootLeft",
            "PelvisRight", "ThighRight", "ShinRight", "FootRight",
        };

        // The same bones with the names Mixamo characters and most retargeting tools use
        static readonly string[] MixamoNames =
        {
            "Hips", "Spine", "Spine1", "Neck",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand",
            "LeftHipJoint", "LeftUpLeg", "LeftLeg", "LeftFoot",
            "RightHipJoint", "RightUpLeg", "RightLeg", "RightFoot",
        };

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly int[] Feet = { Joints.FootLeft, Joints.FootRight, Joints.AnkleLeft, Joints.AnkleRight };

        public class Result
        {
            public int Frames;
            public int PersonId;
            public int PeopleInTake;
            public double AverageErrorCm;   // how far the BVH's joints land from the recorded ones
            public int LiftedFrames;        // frames raised so the feet stay on or above the floor
        }

        class Pose
        {
            public double T;   // seconds
            public readonly Vec3?[] Position = new Vec3?[Joints.Count];
            public readonly Quat?[] Rotation = new Quat?[Joints.Count];   // SDK hierarchical rotations
            public readonly Quat[] Absolute = new Quat[Joints.Count];     // each bone's rotation in the room
        }

        public static Result Export(Dictionary<string, object> take, string path, bool mixamoNames = false)
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
            foreach (var pose in frames) FillAbsolute(pose);

            // The person's own frame relative to the hips (left, up, forward), and each bone's T-pose direction
            var body = PersonFrame(frames);
            var rest = RestDirections(frames, body);
            var toSdk = rest.Select(d => Quat.Between(d, Vec3.Up)).ToArray();   // T-pose direction to the SDK's +Y

            var bvh = new StringBuilder();
            bvh.AppendLine("HIERARCHY");
            WriteHierarchy(bvh, lengths, rest, mixamoNames ? MixamoNames : BoneNames);
            bvh.AppendLine("MOTION");
            bvh.AppendLine("Frames: " + frames.Count);
            bvh.AppendLine("Frame Time: " + (1 / FramesPerSecond).ToString("0.0000000", Invariant));

            double errorSum = 0;
            int errorCount = 0, lifted = 0;
            foreach (var pose in frames)
            {
                // Each bone's rotation in the room, measured from the T-pose, then relative to its parent bone
                var global = new Quat[Joints.Count];
                var local = new Quat[Joints.Count];
                global[Joints.HipCenter] = pose.Absolute[Joints.HipCenter] * body;
                local[Joints.HipCenter] = global[Joints.HipCenter];
                for (int j = 1; j < Joints.Count; j++)
                {
                    global[j] = pose.Absolute[j] * toSdk[j];
                    local[j] = global[Joints.Parent[j]].Inverse * global[j];
                }

                // The angles as written, turned back into rotations, so the check below tests the file itself
                var written = new Quat[Joints.Count];
                var angles = new List<double>();
                foreach (var j in order)
                {
                    local[j].Normalised.ToEulerZXY(out var z, out var x, out var y);
                    angles.Add(z); angles.Add(x); angles.Add(y);
                    written[j] = Quat.FromEulerZXY(z, x, y);
                }

                var hip = pose.Position[Joints.HipCenter].Value;
                var rebuilt = Rebuild(hip, written, lengths, rest);
                for (int j = 1; j < Joints.Count; j++)
                {
                    if (pose.Position[j] == null) continue;
                    errorSum += (rebuilt[j] - pose.Position[j].Value).Length;
                    errorCount++;
                }

                // Never let a foot sink through the floor: fixed bone lengths can push it a little low
                var lowest = Feet.Min(j => rebuilt[j].Y);
                if (lowest < 0) { hip = hip + new Vec3(0, -lowest, 0); lifted++; }

                var values = new List<double> { hip.X, hip.Y, hip.Z };
                values.AddRange(angles);
                bvh.AppendLine(string.Join(" ", values.Select(v => v.ToString("0.#####", Invariant))));
            }

            File.WriteAllText(path, bvh.ToString(), new UTF8Encoding(false));
            return new Result
            {
                Frames = frames.Count,
                PersonId = personId,
                PeopleInTake = people,
                AverageErrorCm = errorCount > 0 ? Math.Round(errorSum / errorCount * 100, 2) : 0,
                LiftedFrames = lifted,
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

        /// <summary>Each bone's rotation in the room: its parent's, then its own SDK rotation relative to that.</summary>
        static void FillAbsolute(Pose pose)
        {
            pose.Absolute[Joints.HipCenter] = (pose.Rotation[Joints.HipCenter] ?? Quat.Identity).Normalised;
            for (int j = 1; j < Joints.Count; j++)
                pose.Absolute[j] = (pose.Absolute[Joints.Parent[j]] * (pose.Rotation[j] ?? Quat.Identity)).Normalised;
        }

        /// <summary>
        /// The person's own left, up and forward, as seen from the hips' SDK frame, averaged over the take.
        /// Up runs from the hip centre to the shoulders; left from the right hip to the left hip.
        /// With left as X and up as Y, forward is X x Y, so a person in this frame faces +Z.
        /// </summary>
        static Quat PersonFrame(List<Pose> frames)
        {
            var up = new Vec3(); var left = new Vec3();
            foreach (var p in frames)
            {
                var toHips = p.Absolute[Joints.HipCenter].Inverse;
                if (p.Position[Joints.ShoulderCenter] != null)
                    up = up + toHips.Rotate((p.Position[Joints.ShoulderCenter].Value - p.Position[Joints.HipCenter].Value).Normalised);
                if (p.Position[Joints.HipLeft] != null && p.Position[Joints.HipRight] != null)
                    left = left + toHips.Rotate((p.Position[Joints.HipLeft].Value - p.Position[Joints.HipRight].Value).Normalised);
            }
            up = up.Normalised;
            left = (left - up * Vec3.Dot(left, up)).Normalised;
            return Quat.FromAxes(left, up, Vec3.Cross(left, up));
        }

        /// <summary>
        /// Each bone's direction in the T-pose, in the person's frame (left +X, up +Y, forward +Z).
        /// Arms point straight out and legs straight down. The spine, neck, collar bones, hip bones and feet
        /// keep the person's own typical direction, averaged over the take, so the rest pose looks like them.
        /// </summary>
        static Vec3[] RestDirections(List<Pose> frames, Quat body)
        {
            var rest = new Vec3[Joints.Count];
            var outLeft = new Vec3(1, 0, 0); var outRight = new Vec3(-1, 0, 0); var down = new Vec3(0, -1, 0);
            foreach (var j in new[] { Joints.ElbowLeft, Joints.WristLeft, Joints.HandLeft }) rest[j] = outLeft;
            foreach (var j in new[] { Joints.ElbowRight, Joints.WristRight, Joints.HandRight }) rest[j] = outRight;
            foreach (var j in new[] { Joints.KneeLeft, Joints.AnkleLeft, Joints.KneeRight, Joints.AnkleRight }) rest[j] = down;

            var typical = new Dictionary<int, Vec3>
            {
                [Joints.Spine] = Vec3.Up, [Joints.ShoulderCenter] = Vec3.Up, [Joints.Head] = Vec3.Up,
                [Joints.ShoulderLeft] = outLeft, [Joints.ShoulderRight] = outRight,
                [Joints.HipLeft] = new Vec3(1, -0.5, 0).Normalised, [Joints.HipRight] = new Vec3(-1, -0.5, 0).Normalised,
                [Joints.FootLeft] = new Vec3(0, -0.4, 1).Normalised, [Joints.FootRight] = new Vec3(0, -0.4, 1).Normalised,
            };
            foreach (var j in typical.Keys)
            {
                var sum = new Vec3();
                foreach (var p in frames)
                {
                    var parent = Joints.Parent[j];
                    if (p.Position[j] == null || p.Position[parent] == null) continue;
                    var toPerson = (p.Absolute[Joints.HipCenter] * body).Inverse;
                    sum = sum + toPerson.Rotate((p.Position[j].Value - p.Position[parent].Value).Normalised);
                }
                rest[j] = sum.Length > 1e-6 ? sum.Normalised : typical[j];
            }
            return rest;
        }

        static void WriteHierarchy(StringBuilder bvh, double[] lengths, Vec3[] rest, string[] names)
        {
            string V(Vec3 v) => $"{v.X.ToString("0.#####", Invariant)} {v.Y.ToString("0.#####", Invariant)} {v.Z.ToString("0.#####", Invariant)}";
            void Bone(int j, int depth)
            {
                var pad = new string('\t', depth);
                var parent = Joints.Parent[j];
                bvh.AppendLine(pad + (parent < 0 ? "ROOT " : "JOINT ") + names[j]);
                bvh.AppendLine(pad + "{");
                bvh.AppendLine($"{pad}\tOFFSET {V(StartOffset(j, lengths, rest))}");
                bvh.AppendLine(pad + (parent < 0
                    ? "\tCHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation"
                    : "\tCHANNELS 3 Zrotation Xrotation Yrotation"));

                var children = Enumerable.Range(0, Joints.Count).Where(c => Joints.Parent[c] == j).ToList();
                foreach (var c in children) Bone(c, depth + 1);
                if (children.Count == 0)
                {
                    bvh.AppendLine(pad + "\tEnd Site");
                    bvh.AppendLine(pad + "\t{");
                    bvh.AppendLine($"{pad}\t\tOFFSET {V(rest[j] * lengths[j])}");
                    bvh.AppendLine(pad + "\t}");
                }
                bvh.AppendLine(pad + "}");
            }
            Bone(Joints.HipCenter, 0);
        }

        /// <summary>
        /// Where a bone starts, relative to its parent BVH joint in the T-pose: at the end of the parent bone,
        /// or at the hips for bones leaving the hips (the spine 4 mm up, so Hips has some length).
        /// </summary>
        static Vec3 StartOffset(int j, double[] lengths, Vec3[] rest)
        {
            var parent = Joints.Parent[j];
            if (parent < 0) return new Vec3();
            if (parent == Joints.HipCenter) return j == Joints.Spine ? new Vec3(0, RootNubMetres, 0) : new Vec3();
            return rest[parent] * lengths[parent];
        }

        /// <summary>
        /// Rebuilds every joint position from the hips and the rotations as written, the way Blender will,
        /// bone by bone. Used to check the file against the recording, and to keep the feet above the floor.
        /// </summary>
        static Vec3[] Rebuild(Vec3 hip, Quat[] local, double[] lengths, Vec3[] rest)
        {
            var global = new Quat[Joints.Count];
            var start = new Vec3[Joints.Count];     // where each BVH joint sits
            var end = new Vec3[Joints.Count];       // where each bone ends: the Kinect joint position
            global[Joints.HipCenter] = local[Joints.HipCenter];
            start[Joints.HipCenter] = hip;
            end[Joints.HipCenter] = hip;
            for (int j = 1; j < Joints.Count; j++)
            {
                var parent = Joints.Parent[j];
                global[j] = global[parent] * local[j];
                start[j] = start[parent] + global[parent].Rotate(StartOffset(j, lengths, rest));
                end[j] = start[j] + global[j].Rotate(rest[j] * lengths[j]);
            }
            return end;
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
    }
}
