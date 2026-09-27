using System;
using System.Collections.Generic;
using KinectBridge.Skeleton;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// Skeletons for the mock sensor's two pretend people: player 1 walks with swinging arms and legs,
    /// player 2 stands and waves. Joints get a little random jitter, like the real sensor, and the
    /// smoothing setting filters it, so the page's smoothing control has a visible effect in mock mode too.
    /// The SDK does the real smoothing; this filter only imitates it.
    /// </summary>
    class MockSkeleton
    {
        const double JitterMetres = 0.012;
        const double FloorY = -MockScene.CameraHeight;

        readonly Random random = new Random();
        readonly Dictionary<int, double[]> smoothed = new Dictionary<int, double[]>();   // per player: x, y, z per joint

        // Standing pose relative to the person's centre line and the floor: sideways offset, height above the floor, forward offset
        static readonly double[,] Pose =
        {
            { 0.00, 0.95, 0.00 },   // hipCenter
            { 0.00, 1.15, 0.00 },   // spine
            { 0.00, 1.42, 0.00 },   // shoulderCenter
            { 0.00, 1.62, 0.00 },   // head
            { -0.18, 1.40, 0.00 },  // shoulderLeft
            { -0.22, 1.12, 0.00 },  // elbowLeft
            { -0.24, 0.88, -0.02 }, // wristLeft
            { -0.25, 0.80, -0.03 }, // handLeft
            { 0.18, 1.40, 0.00 },   // shoulderRight
            { 0.22, 1.12, 0.00 },   // elbowRight
            { 0.24, 0.88, -0.02 },  // wristRight
            { 0.25, 0.80, -0.03 },  // handRight
            { -0.10, 0.92, 0.00 },  // hipLeft
            { -0.11, 0.48, 0.00 },  // kneeLeft
            { -0.11, 0.08, 0.02 },  // ankleLeft
            { -0.11, 0.03, -0.08 }, // footLeft
            { 0.10, 0.92, 0.00 },   // hipRight
            { 0.11, 0.48, 0.00 },   // kneeRight
            { 0.11, 0.08, 0.02 },   // ankleRight
            { 0.11, 0.03, -0.08 },  // footRight
        };

        public SkeletonData Build(double seconds, SkeletonSettings settings)
        {
            var data = new SkeletonData
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Floor = new[] { 0f, 1f, 0f, (float)MockScene.CameraHeight },   // flat floor 1 m below the Kinect
            };

            foreach (var person in MockScene.People(seconds))
            {
                var raw = person.Player == 1 ? Walking(person, seconds) : Waving(person, seconds);
                var joints = Smooth(person.Player, raw, settings.Smoothing);
                var tracked = settings.Mode == TrackingMode.Seated ? Joints.Seated : AllJoints;

                var body = new Body { Id = 100 + person.Player, Player = person.Player };
                foreach (var j in tracked)
                {
                    double x = joints[j * 3], y = joints[j * 3 + 1], z = joints[j * 3 + 2];
                    var image = MockScene.Project(x, y, z);   // the mock's colour and depth cameras line up exactly
                    body.Joints[j] = new BodyJoint
                    {
                        X = (float)x, Y = (float)y, Z = (float)z,
                        ColourX = image.X, ColourY = image.Y, DepthX = image.X, DepthY = image.Y,
                        // The walker's far-side hand is hidden behind the body now and then: shown as inferred
                        Inferred = person.Player == 1 && j == Joints.HandLeft && Math.Sin(seconds * 4) > 0.6,
                    };
                }
                data.Bodies.Add(body);
            }
            return data;
        }

        static readonly int[] AllJoints = BuildAll();
        static int[] BuildAll() { var all = new int[Joints.Count]; for (int i = 0; i < all.Length; i++) all[i] = i; return all; }

        /// <summary>Arms and legs swing in opposite pairs, one full step every second or so.</summary>
        double[] Walking(MockScene.Person p, double seconds)
        {
            var joints = Place(p);
            var swing = Math.Sin(seconds * 5.5);
            Swing(joints, new[] { Joints.KneeLeft, Joints.AnkleLeft, Joints.FootLeft }, 0.18 * swing, 0.48);
            Swing(joints, new[] { Joints.KneeRight, Joints.AnkleRight, Joints.FootRight }, -0.18 * swing, 0.48);
            Swing(joints, new[] { Joints.ElbowLeft, Joints.WristLeft, Joints.HandLeft }, -0.12 * swing, 1.40);
            Swing(joints, new[] { Joints.ElbowRight, Joints.WristRight, Joints.HandRight }, 0.12 * swing, 1.40);
            return joints;
        }

        /// <summary>The right arm raised, forearm waving side to side.</summary>
        double[] Waving(MockScene.Person p, double seconds)
        {
            var joints = Place(p);
            var shoulderX = joints[Joints.ShoulderRight * 3];
            var shoulderY = joints[Joints.ShoulderRight * 3 + 1];
            var elbowX = shoulderX + 0.22;
            var elbowY = shoulderY + 0.05;
            var angle = Math.PI / 2 + 0.5 * Math.Sin(seconds * 6);   // forearm points up, rocking about 30 degrees each way
            Set(joints, Joints.ElbowRight, elbowX, elbowY);
            Set(joints, Joints.WristRight, elbowX + 0.25 * Math.Cos(angle), elbowY + 0.25 * Math.Sin(angle));
            Set(joints, Joints.HandRight, elbowX + 0.33 * Math.Cos(angle), elbowY + 0.33 * Math.Sin(angle));
            return joints;
        }

        /// <summary>The standing pose, moved to where the person is, with jitter.</summary>
        double[] Place(MockScene.Person p)
        {
            var joints = new double[Joints.Count * 3];
            for (int j = 0; j < Joints.Count; j++)
            {
                joints[j * 3] = p.X + Pose[j, 0] + Noise();
                joints[j * 3 + 1] = FloorY + Pose[j, 1] + Noise();
                joints[j * 3 + 2] = p.Z + Pose[j, 2] + Noise();
            }
            return joints;
        }

        /// <summary>Swings a limb sideways (along the direction of walking), more at the far end than near the pivot.</summary>
        static void Swing(double[] joints, int[] limb, double amount, double pivotHeight)
        {
            foreach (var j in limb)
            {
                var drop = Math.Abs(pivotHeight - (joints[j * 3 + 1] - FloorY));
                joints[j * 3] += amount * drop / 0.45;
            }
        }

        static void Set(double[] joints, int j, double x, double y)
        {
            joints[j * 3] = x;
            joints[j * 3 + 1] = y;
        }

        double Noise() => (random.NextDouble() * 2 - 1) * JitterMetres;

        /// <summary>Blends each new position with the last one: the heavier the setting, the more of the old position is kept.</summary>
        double[] Smooth(int player, double[] raw, Smoothing smoothing)
        {
            var keep = smoothing == Smoothing.Heavy ? 0.8 : smoothing == Smoothing.Light ? 0.5 : 0.0;
            if (!smoothed.TryGetValue(player, out var last) || keep == 0)
            {
                smoothed[player] = raw;
                return raw;
            }
            for (int i = 0; i < raw.Length; i++) last[i] = keep * last[i] + (1 - keep) * raw[i];
            return last;
        }
    }
}
