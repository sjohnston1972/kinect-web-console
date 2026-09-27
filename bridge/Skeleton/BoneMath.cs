using System;

namespace KinectBridge.Skeleton
{
    /// <summary>A 3D direction or position, in metres.</summary>
    struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalised { get { var l = Length; return l > 1e-9 ? this * (1 / l) : new Vec3(0, 1, 0); } }
        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static readonly Vec3 Up = new Vec3(0, 1, 0);
    }

    /// <summary>A rotation, as a unit quaternion.</summary>
    struct Quat
    {
        public double X, Y, Z, W;
        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        /// <summary>Doing b first, then a.</summary>
        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public Quat Inverse => new Quat(-X, -Y, -Z, W);

        public Quat Normalised
        {
            get
            {
                var l = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return l > 1e-9 ? new Quat(X / l, Y / l, Z / l, W / l) : Identity;
            }
        }

        public Vec3 Rotate(Vec3 v)
        {
            var p = this * new Quat(v.X, v.Y, v.Z, 0) * Inverse;
            return new Vec3(p.X, p.Y, p.Z);
        }

        /// <summary>The smallest rotation that turns direction from into direction to.</summary>
        public static Quat Between(Vec3 from, Vec3 to)
        {
            from = from.Normalised;
            to = to.Normalised;
            var d = Vec3.Dot(from, to);
            if (d < -0.999999)
            {
                // Opposite directions: turn half a circle about any axis at right angles to them
                var axis = Vec3.Cross(new Vec3(1, 0, 0), from);
                if (axis.Length < 1e-6) axis = Vec3.Cross(new Vec3(0, 0, 1), from);
                axis = axis.Normalised;
                return new Quat(axis.X, axis.Y, axis.Z, 0);
            }
            var c = Vec3.Cross(from, to);
            return new Quat(c.X, c.Y, c.Z, 1 + d).Normalised;
        }

        /// <summary>The rotation whose columns are these three at-right-angles axes.</summary>
        public static Quat FromAxes(Vec3 x, Vec3 y, Vec3 z)
        {
            double m00 = x.X, m10 = x.Y, m20 = x.Z, m01 = y.X, m11 = y.Y, m21 = y.Z, m02 = z.X, m12 = z.Y, m22 = z.Z;
            double trace = m00 + m11 + m22;
            if (trace > 0)
            {
                var s = Math.Sqrt(trace + 1) * 2;
                return new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, s / 4).Normalised;
            }
            if (m00 > m11 && m00 > m22)
            {
                var s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
                return new Quat(s / 4, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s).Normalised;
            }
            if (m11 > m22)
            {
                var s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
                return new Quat((m01 + m10) / s, s / 4, (m12 + m21) / s, (m02 - m20) / s).Normalised;
            }
            var t = Math.Sqrt(1 + m22 - m00 - m11) * 2;
            return new Quat((m02 + m20) / t, (m12 + m21) / t, t / 4, (m10 - m01) / t).Normalised;
        }

        /// <summary>
        /// Angles in degrees for BVH's "Zrotation Xrotation Yrotation" order: turn about Z, then X, then Y
        /// (as matrices, R = Rz * Rx * Ry).
        /// </summary>
        public void ToEulerZXY(out double z, out double x, out double y)
        {
            // The rotation matrix entries needed, from the quaternion
            double m01 = 2 * (X * Y - W * Z), m11 = 1 - 2 * (X * X + Z * Z);
            double m20 = 2 * (X * Z - W * Y), m21 = 2 * (Y * Z + W * X), m22 = 1 - 2 * (X * X + Y * Y);
            double m00 = 1 - 2 * (Y * Y + Z * Z), m10 = 2 * (X * Y + W * Z);

            m21 = Math.Max(-1, Math.Min(1, m21));
            x = Math.Asin(m21);
            if (Math.Abs(m21) < 0.99999)
            {
                y = Math.Atan2(-m20, m22);
                z = Math.Atan2(-m01, m11);
            }
            else
            {
                // Pointing straight along X: Z and Y turn about the same axis, so put it all in Z
                y = 0;
                z = Math.Atan2(m10, m00);
            }
            x *= 180 / Math.PI; y *= 180 / Math.PI; z *= 180 / Math.PI;
        }

        public static Quat FromEulerZXY(double zDeg, double xDeg, double yDeg)
        {
            Quat Axis(double deg, double ax, double ay, double az)
            {
                var h = deg * Math.PI / 360;
                return new Quat(ax * Math.Sin(h), ay * Math.Sin(h), az * Math.Sin(h), Math.Cos(h));
            }
            return Axis(zDeg, 0, 0, 1) * Axis(xDeg, 1, 0, 0) * Axis(yDeg, 0, 1, 0);
        }
    }

    /// <summary>
    /// Works out bone rotations from joint positions, following the SDK's convention, for the mock sensor
    /// (the real Kinect supplies its own). Each bone points along +Y of its own frame; the hips' frame has
    /// +Y up the spine and +X from the left hip to the right hip.
    /// </summary>
    static class BoneMath
    {
        public static void FillRotations(Body body)
        {
            var joints = body.Joints;
            var absolute = new Quat[Joints.Count];
            var hip = joints[Joints.HipCenter];
            if (hip == null || joints[Joints.Spine] == null || joints[Joints.HipLeft] == null || joints[Joints.HipRight] == null) return;

            var up = (Pos(joints[Joints.Spine]) - Pos(hip)).Normalised;
            var across = Pos(joints[Joints.HipRight]) - Pos(joints[Joints.HipLeft]);
            var xAxis = (across - up * Vec3.Dot(across, up)).Normalised;
            var zAxis = Vec3.Cross(xAxis, up);
            absolute[Joints.HipCenter] = Quat.FromAxes(xAxis, up, zAxis);
            hip.Rotation = absolute[Joints.HipCenter];

            // Parents come before children in joint order, so one pass works down the body
            for (int j = 1; j < Joints.Count; j++)
            {
                var parent = Joints.Parent[j];
                var joint = joints[j];
                if (joint == null || joints[parent] == null) continue;

                // The parent's frame: the bone ending at the parent, or the hips' frame for bones leaving the hips
                var parentFrame = absolute[parent];
                var direction = Pos(joint) - Pos(joints[parent]);
                absolute[j] = Quat.Between(parentFrame.Rotate(Vec3.Up), direction) * parentFrame;
                joint.Rotation = (parentFrame.Inverse * absolute[j]).Normalised;
            }
        }

        public static Vec3 Pos(BodyJoint j) => new Vec3(j.X, j.Y, j.Z);
    }
}
