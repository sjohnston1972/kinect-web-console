using System;
using KinectBridge.Sensor;

namespace KinectBridge.Streams
{
    /// <summary>
    /// Paints a depth frame as a picture: near is warm (red, orange), far is cool (green, blue).
    /// The same colour stops are in web/js/depthcolours.js for the legend and the point cloud;
    /// change both together.
    ///
    /// With "highlight people" on, everything except tracked people turns dim grey,
    /// so the people stand out in full colour.
    /// </summary>
    static class DepthColouriser
    {
        // Distance along the range (0 = 0.8 m, 1 = 4 m) and the colour at that point, as red, green, blue
        static readonly double[] StopAt = { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 };
        static readonly byte[,] StopColour =
        {
            { 255, 70, 40 },
            { 255, 150, 30 },
            { 240, 225, 60 },
            { 70, 200, 120 },
            { 40, 160, 220 },
            { 70, 70, 200 },
        };
        static readonly byte[] NoReading = { 20, 20, 24 };

        const int LookupSize = 8192;   // covers every depth the sensor can report, in mm
        static readonly byte[] LutR = new byte[LookupSize], LutG = new byte[LookupSize], LutB = new byte[LookupSize];

        static DepthColouriser()
        {
            for (int mm = 0; mm < LookupSize; mm++)
            {
                byte r, g, b;
                if (mm == 0) { r = NoReading[0]; g = NoReading[1]; b = NoReading[2]; }
                else
                {
                    double t = (mm - DepthFrame.MinDepth) / (double)(DepthFrame.MaxDepth - DepthFrame.MinDepth);
                    ColourAt(Math.Max(0, Math.Min(1, t)), out r, out g, out b);
                    if (mm > DepthFrame.MaxDepth || mm < DepthFrame.MinDepth)
                    {
                        // Outside the reliable range: shown dimmed, so it reads as "less trustworthy"
                        r = (byte)(r / 2); g = (byte)(g / 2); b = (byte)(b / 2);
                    }
                }
                LutR[mm] = r; LutG[mm] = g; LutB[mm] = b;
            }
        }

        static void ColourAt(double t, out byte r, out byte g, out byte b)
        {
            int i = 0;
            while (i < StopAt.Length - 2 && t > StopAt[i + 1]) i++;
            double f = (t - StopAt[i]) / (StopAt[i + 1] - StopAt[i]);
            r = (byte)(StopColour[i, 0] + f * (StopColour[i + 1, 0] - StopColour[i, 0]));
            g = (byte)(StopColour[i, 1] + f * (StopColour[i + 1, 1] - StopColour[i, 1]));
            b = (byte)(StopColour[i, 2] + f * (StopColour[i + 1, 2] - StopColour[i, 2]));
        }

        /// <summary>Fills bgra (4 bytes per pixel) from the depth frame.</summary>
        public static void Paint(DepthFrame frame, byte[] bgra, bool highlightPeople)
        {
            var depth = frame.Depth;
            var player = frame.Player;
            for (int i = 0, o = 0; i < depth.Length; i++, o += 4)
            {
                int mm = depth[i];
                if (mm < 0 || mm >= LookupSize) mm = 0;
                byte r = LutR[mm], g = LutG[mm], b = LutB[mm];

                if (highlightPeople && player[i] == 0)
                {
                    // Dim grey, keeping a little brightness so the room is still readable
                    byte grey = (byte)((r * 30 + g * 59 + b * 11) / 250);
                    r = g = b = grey;
                }

                bgra[o] = b;
                bgra[o + 1] = g;
                bgra[o + 2] = r;
                bgra[o + 3] = 255;
            }
        }
    }
}
