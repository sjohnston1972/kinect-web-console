using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// Draws the mock sensor's pretend room: a back wall 3.5 m away, a floor, a box on the left,
    /// and a person-sized shape walking side to side 2 m away. Colour and depth use the same
    /// camera maths, so the person lines up in both, as it would with the real Kinect.
    /// </summary>
    class MockScene : IDisposable
    {
        const int W = 640, H = 480;
        const double Focal = 571;            // pixels, close to the Kinect's depth camera at 640x480
        const double CameraHeight = 1.0;     // metres above the floor
        const short WallMm = 3500;
        const double PersonZ = 2.0, PersonHalfWidth = 0.25, PersonTop = 1.75;

        readonly Bitmap canvas = new Bitmap(W, H, PixelFormat.Format32bppRgb);
        readonly Graphics g;
        readonly Font labelFont = new Font("Segoe UI", 16, FontStyle.Bold);

        public MockScene()
        {
            g = Graphics.FromImage(canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        /// <summary>Where the walking person is, in metres left or right of centre.</summary>
        public static double PersonX(double seconds) => 0.9 * Math.Sin(seconds * 0.6);

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

            // The box, drawn where DrawDepth puts it
            using (var box = new SolidBrush(Color.FromArgb(190, 140, 60))) g.FillRectangle(box, BoxRect());

            // The person
            var p = PersonRect(seconds);
            using (var body = new SolidBrush(Color.FromArgb(230, 120, 70))) g.FillEllipse(body, p);

            g.DrawString("MOCK SENSOR  " + DateTime.Now.ToString("HH:mm:ss.f"), labelFont, Brushes.White, 12, 40);

            var data = canvas.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(data.Scan0, bgra, 0, bgra.Length); }
            finally { canvas.UnlockBits(data); }
        }

        public void DrawDepth(short[] depth, byte[] player, double seconds)
        {
            var box = BoxRect();
            var person = PersonRect(seconds);
            double cx = person.X + person.Width / 2.0, cy = person.Y + person.Height / 2.0;
            double rx = person.Width / 2.0, ry = person.Height / 2.0;

            for (int v = 0; v < H; v++)
            {
                // Floor distance from the camera for this row, or the wall if the floor is further away
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

                    if (box.Contains(u, v)) d = 1500;

                    // The person is rounded: nearest at the middle, curving away at the edges
                    double nx = (u - cx) / rx, ny = (v - cy) / ry;
                    double r2 = nx * nx + ny * ny;
                    if (r2 < 1)
                    {
                        d = (short)(PersonZ * 1000 - 150 * Math.Sqrt(1 - r2));
                        who = 1;
                    }

                    // The real sensor has no reading at the far left edge; copy that so the page handles gaps
                    if (u < 8) d = 0;

                    depth[i] = d;
                    player[i] = who;
                }
            }
        }

        static Rectangle BoxRect() => new Rectangle(70, 290, 110, 120);

        static RectangleF PersonRect(double seconds)
        {
            var x = PersonX(seconds);
            float left = (float)(W / 2 + Focal * (x - PersonHalfWidth) / PersonZ);
            float right = (float)(W / 2 + Focal * (x + PersonHalfWidth) / PersonZ);
            float top = (float)(H / 2 - Focal * (PersonTop - CameraHeight) / PersonZ);
            float bottom = (float)(H / 2 + Focal * CameraHeight / PersonZ);
            return new RectangleF(left, top, right - left, bottom - top);
        }

        public void Dispose()
        {
            g.Dispose();
            canvas.Dispose();
            labelFont.Dispose();
        }
    }
}
