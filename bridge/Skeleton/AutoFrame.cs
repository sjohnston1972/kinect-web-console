using System;
using System.Linq;

namespace KinectBridge.Skeleton
{
    /// <summary>
    /// Works out a tilt angle that fits a tracked person's head and feet into the picture.
    ///
    /// Joint positions are measured from the Kinect itself, so the angle of a joint above or below the middle of
    /// the picture is atan(height / distance). Tilting by the average of the head's and feet's angles centres them.
    /// The depth camera sees about 43 degrees top to bottom; if the person is taller than that, the head is kept in.
    /// </summary>
    static class AutoFrame
    {
        const double HalfView = 21.5;          // degrees from the middle to the top or bottom of the picture
        const double Margin = 2.5;             // keep head and feet this many degrees inside the edges
        const double AboveHeadJoint = 0.12;    // metres from the head joint to the top of the head
        const double TypicalHeight = 1.7;      // metres, when neither feet nor floor can be seen
        const int SmallestMove = 2;            // degrees; smaller changes are not worth moving the motor for

        /// <summary>
        /// The angle to tilt to, or null with a plain-English reason. Uses the person numbered 1 (the first to appear).
        /// </summary>
        public static int? Target(SkeletonData data, int currentTilt, out string note)
        {
            var body = data?.Bodies.OrderBy(b => b.Person).FirstOrDefault();
            var head = body?.Joints[Joints.Head];
            if (head == null)
            {
                note = "Nobody is tracked, so there is no one to frame. Stand in view facing the Kinect first.";
                return null;
            }

            double Angle(double y, double z) => Math.Atan2(y, z) * 180 / Math.PI;
            var top = Angle(head.Y + AboveHeadJoint, head.Z);

            // The feet: tracked if possible, otherwise the floor under the person, otherwise a typical height
            double bottom;
            var feet = new[] { Joints.FootLeft, Joints.FootRight, Joints.AnkleLeft, Joints.AnkleRight }
                .Select(j => body.Joints[j]).Where(j => j != null && !j.Inferred).ToList();
            if (feet.Count > 0) bottom = feet.Min(f => Angle(f.Y, f.Z));
            else if (data.Floor != null && Math.Abs(data.Floor[1]) > 0.5)
            {
                var f = data.Floor;   // the floor's height (y) at the person's distance, from Ax + By + Cz + D = 0
                bottom = Angle(-(f[0] * head.X + f[2] * head.Z + f[3]) / f[1], head.Z);
            }
            else bottom = Angle(head.Y + AboveHeadJoint - TypicalHeight, head.Z);

            var span = top - bottom;
            double change = span <= 2 * (HalfView - Margin)
                ? (top + bottom) / 2                    // both fit: centre them
                : top - (HalfView - Margin);           // too tall to fit: keep the head just inside the top

            var fits = span <= 2 * (HalfView - Margin);
            var target = (int)Math.Round(Math.Max(-27, Math.Min(27, currentTilt + change)));
            if (Math.Abs(target - currentTilt) < SmallestMove)
            {
                note = fits
                    ? "Already framed: head and feet are in view."
                    : "Already as good as it gets from here: the head is in view, but the person is too tall to fit from this distance. Stepping back fits the feet too.";
                return null;
            }
            note = fits
                ? $"Tilting to {target} degrees to fit head and feet in view."
                : $"Tilting to {target} degrees. The person is too tall to fit from this distance, so the head is kept in view; stepping back fits the feet too.";
            return target;
        }
    }
}
