using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// Draws the mock sensor's pretend room: a back wall 3.5 m away, a floor, a box on the left 1.5 m away,
    /// and two people: player 1 walks side to side 2 m away, player 2 stands further back and waves.
    /// Colour, depth and the mock skeletons (MockSkeleton) all use the same camera maths,
    /// so everything lines up, as it does with the real Kinect.
    /// </summary>
    class MockScene : IDisposable
    {
        public const int W = 640, H = 480;
        public const double Focal = 571;            // pixels, close to the Kinect's depth camera at 640x480
        public const double CameraHeight = 1.0;     // metres above the floor
        const short WallMm = 3500;
        const short BoxMm = 1500;
        const double HalfWidth = 0.25, Height = 1.75;

        /// <summary>A person-shaped oval standing on the floor.</summary>
        public struct Person
        {
            public double X, Z;    // metres: sideways from centre, distance from the Kinect
            public byte Player;
            public Color Colour;
        }

        readonly Bitmap canvas = new Bitmap(W, H, PixelFormat.Format32bppRgb);
        readonly Graphics g;
        readonly Font labelFont = new Font("Segoe UI", 16, FontStyle.Bold);

        public MockScene()
        {
            g = Graphics.FromImage(canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        /// <summary>Where both people are at this moment, furthest first.</summary>
        public static Person[] People(double seconds)
        {
            return new[]
            {
                new Person { X = -0.8, Z = 2.8, Player = 2, Colour = Color.FromArgb(80, 140, 230) },
                new Person { X = 0.9 * Math.Sin(seconds * 0.6), Z = 2.0, Player = 1, Colour = Color.FromArgb(230, 120, 70) },
            };
        }

        /// <summary>Where a point in the room (metres) lands in the picture, in pixels.</summary>
        public static PointF Project(double x, double y, double z)
        {
            return new PointF((float)(W / 2 + Focal * x / z), (float)(H / 2 - Focal * y / z));
        }

        public void DrawColour(byte[] bgra, double seconds)
        {
            var horizon = H / 2;
            using (var wall = new LinearGradientBrush(new Rectangle(0, 0, W, horizon), Color.FromArgb(40, 52, 80), Color.FromArgb(70, 88, 120), 90f))
                g.FillRectangle(wall, 0, 0, W, horizon);
            using (var floor = new LinearGradientBrush(new Rectangle(0, horizon, W, H - horizon), Color.FromArgb(90, 80, 70), Color.FromArgb(150, 130, 110), 90f))
                g.FillRectangle(floor, 0, horizon, W, H - horizon);

            // Colour bars across the top, handy for spotting colour channel mix-ups
            var bars = new[] { Color.White, Color.Yellow, Color.Cyan, Color.Lime, Color.Magenta, Color.Red, Color.Blue };
            for (int i = 0; i < bars.Length; i++)
                using (var b = new SolidBrush(bars[i])) g.FillRectangle(b, i * W / bars.Length, 0, W / bars.Length + 1, 28);

            // Furthest first, so nearer things cover them. The box is nearest of all.
            foreach (var person in People(seconds))
                using (var body = new SolidBrush(person.Colour)) g.FillEllipse(body, Outline(person));
            using (var box = new SolidBrush(Color.FromArgb(190, 140, 60))) g.FillRectangle(box, BoxRect());

            g.DrawString("MOCK SENSOR  " + DateTime.Now.ToString("HH:mm:ss.f"), labelFont, Brushes.White, 12, 40);

            var data = canvas.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(data.Scan0, bgra, 0, bgra.Length); }
            finally { canvas.UnlockBits(data); }
        }

        public void DrawDepth(short[] depth, byte[] player, double seconds)
        {
            var box = BoxRect();
            var people = People(seconds);
            var outlines = new RectangleF[people.Length];
            for (int p = 0; p < people.Length; p++) outlines[p] = Outline(people[p]);

            for (int v = 0; v < H; v++)
            {
                // Floor distance for this row, or the wall if the floor is further away
                short background = WallMm;
                if (v > H / 2)
                {
                    var floor = CameraHeight * Focal / (v - H / 2) * 1000;
                    if (floor < WallMm) background = (short)floor;
                }

                for (int u = 0; u < W; u++)
                {
                    int i = v * W + u;
                    short d = background;
                    byte who = 0;

                    // Each person is rounded: nearest in the middle, curving away at the edges. Nearest thing wins.
                    for (int p = 0; p < people.Length; p++)
                    {
                        var o = outlines[p];
                        double nx = (u - (o.X + o.Width / 2)) / (o.Width / 2);
                        double ny = (v - (o.Y + o.Height / 2)) / (o.Height / 2);
                        double r2 = nx * nx + ny * ny;
                        if (r2 >= 1) continue;
                        var personMm = (short)(people[p].Z * 1000 - 150 * Math.Sqrt(1 - r2));
                        if (personMm < d) { d = personMm; who = people[p].Player; }
                    }
                    if (box.Contains(u, v) && BoxMm < d) { d = BoxMm; who = 0; }

                    // The real sensor has no reading at the far left edge; copy that so the page handles gaps
                    if (u < 8) { d = 0; who = 0; }

                    depth[i] = d;
                    player[i] = who;
                }
            }
        }

        static Rectangle BoxRect() => new Rectangle(70, 290, 110, 120);

        /// <summary>The oval a person covers in the picture: floor to head height, shoulder width.</summary>
        static RectangleF Outline(Person p)
        {
            var topLeft = Project(p.X - HalfWidth, Height - CameraHeight, p.Z);
            var bottomRight = Project(p.X + HalfWidth, -CameraHeight, p.Z);
            return new RectangleF(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        }

        public void Dispose()
        {
            g.Dispose();
            canvas.Dispose();
            labelFont.Dispose();
        }
    }
}
