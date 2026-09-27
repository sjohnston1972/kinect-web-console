using System.Collections.Generic;

namespace KinectBridge.Skeleton
{
    /// <summary>
    /// The 20 joints the Kinect tracks, in the SDK's own order (Microsoft.Kinect.JointType),
    /// so a joint's number here is the same as the SDK's. The names are what the page sees.
    /// </summary>
    static class Joints
    {
        public const int Count = 20;

        public static readonly string[] Names =
        {
            "hipCenter", "spine", "shoulderCenter", "head",
            "shoulderLeft", "elbowLeft", "wristLeft", "handLeft",
            "shoulderRight", "elbowRight", "wristRight", "handRight",
            "hipLeft", "kneeLeft", "ankleLeft", "footLeft",
            "hipRight", "kneeRight", "ankleRight", "footRight",
        };

        public const int HipCenter = 0, Spine = 1, ShoulderCenter = 2, Head = 3,
            ShoulderLeft = 4, ElbowLeft = 5, WristLeft = 6, HandLeft = 7,
            ShoulderRight = 8, ElbowRight = 9, WristRight = 10, HandRight = 11,
            HipLeft = 12, KneeLeft = 13, AnkleLeft = 14, FootLeft = 15,
            HipRight = 16, KneeRight = 17, AnkleRight = 18, FootRight = 19;

        /// <summary>
        /// The joint each joint hangs from (the SDK's hierarchy), or -1 for the hip centre, which is the root.
        /// The bone ending at a joint runs from its parent to it.
        /// </summary>
        public static readonly int[] Parent =
        {
            -1, HipCenter, Spine, ShoulderCenter,
            ShoulderCenter, ShoulderLeft, ElbowLeft, WristLeft,
            ShoulderCenter, ShoulderRight, ElbowRight, WristRight,
            HipCenter, HipLeft, KneeLeft, AnkleLeft,
            HipCenter, HipRight, KneeRight, AnkleRight,
        };

        /// <summary>The 10 upper-body joints tracked in seated mode.</summary>
        public static readonly int[] Seated =
        {
            Head, ShoulderCenter, ShoulderLeft, ElbowLeft, WristLeft, HandLeft,
            ShoulderRight, ElbowRight, WristRight, HandRight,
        };
    }

    /// <summary>One joint: where it is in the room, where it appears in each picture, and how sure the Kinect is.</summary>
    class BodyJoint
    {
        /// <summary>Metres from the Kinect: X sideways, Y up, Z straight out from the sensor.</summary>
        public float X, Y, Z;

        /// <summary>Pixel position in the 640x480 colour picture and in the 640x480 depth picture.</summary>
        public float ColourX, ColourY, DepthX, DepthY;

        /// <summary>True if the Kinect is guessing this joint (for example, a hand hidden behind the body).</summary>
        public bool Inferred;

        /// <summary>
        /// Rotation of the bone ending at this joint, relative to the bone it hangs from, as a quaternion (x, y, z, w).
        /// Each bone points along its own +Y. For the hip centre it is the whole body's rotation relative to the Kinect.
        /// From the SDK's hierarchical bone orientations; worked out from positions for the mock sensor.
        /// </summary>
        public Quat Rotation = Quat.Identity;
    }

    /// <summary>One tracked person.</summary>
    class Body
    {
        /// <summary>The SDK's tracking number, stable while the person stays in view.</summary>
        public int Id;

        /// <summary>1 to 6. The SDK's own number, which matches the person number marked in each depth pixel.</summary>
        public int Player;

        /// <summary>
        /// 1, 2, ... in the order people appeared, kept while they stay tracked. The page's colours and labels use this,
        /// so one person alone is always Person 1. Set by SkeletonPump.
        /// </summary>
        public int Person;

        /// <summary>Indexed by joint number. Null where the joint is not tracked at all (for example, legs in seated mode).</summary>
        public readonly BodyJoint[] Joints = new BodyJoint[Skeleton.Joints.Count];
    }

    /// <summary>Everything from one skeleton frame.</summary>
    class SkeletonData
    {
        public long Timestamp;
        public readonly List<Body> Bodies = new List<Body>();

        /// <summary>The floor as a plane (A, B, C, D, where Ax + By + Cz + D = 0), or null if the Kinect cannot see it.</summary>
        public float[] Floor;
    }
}
