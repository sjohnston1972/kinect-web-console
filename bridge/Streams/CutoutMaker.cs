using System;
using KinectBridge.Sensor;

namespace KinectBridge.Streams
{
    /// <summary>
    /// The virtual green screen: keeps only the people in the colour picture.
    ///
    /// The Kinect marks which depth pixels belong to a tracked person. Each depth pixel is mapped to the colour
    /// pixel that sees the same spot (the cameras sit a few centimetres apart), and those colour pixels, plus
    /// their neighbours to close the small gaps the mapping leaves, are kept. Everything else becomes a plain
    /// background: green for the live view, or fully transparent for a PNG.
    /// </summary>
    class CutoutMaker
    {
        const int W = ColourFrame.Width, H = ColourFrame.Height;
        public static readonly byte[] Green = { 64, 177, 0 };    // blue, green, red: a standard chroma green

        readonly ISensor sensor;
        readonly object gate = new object();
        readonly int[] colourIndex = new int[DepthFrame.Width * DepthFrame.Height];
        readonly byte[] mask = new byte[W * H];
        readonly byte[] grown = new byte[W * H];

        public CutoutMaker(ISensor sensor)
        {
            this.sensor = sensor;
        }

        /// <summary>
        /// Fills bgra with the colour picture's people on the background. With transparent, the background has
        /// alpha 0 (for a PNG); otherwise it is green. Returns how many pixels are people, or -1 if mapping failed.
        /// </summary>
        public int Make(ColourFrame colour, DepthFrame depth, byte[] bgra, bool transparent)
        {
            lock (gate)
            {
                if (!sensor.MapDepthToColour(depth, colourIndex)) return -1;

                Array.Clear(mask, 0, mask.Length);
                for (int i = 0; i < colourIndex.Length; i++)
                {
                    var c = colourIndex[i];
                    if (c >= 0 && depth.Player[i] != 0) mask[c] = 1;
                }

                // Grow the mask by one pixel each way: the mapping leaves pinholes between mapped pixels
                Array.Clear(grown, 0, grown.Length);
                int people = 0;
                for (int y = 1; y < H - 1; y++)
                    for (int x = 1; x < W - 1; x++)
                    {
                        int i = y * W + x;
                        if (mask[i] == 0 && mask[i - 1] == 0 && mask[i + 1] == 0 && mask[i - W] == 0 && mask[i + W] == 0) continue;
                        grown[i] = 1;
                        people++;
                    }

                var pixels = colour.Pixels;
                for (int i = 0, o = 0; i < grown.Length; i++, o += 4)
                {
                    if (grown[i] != 0)
                    {
                        bgra[o] = pixels[o]; bgra[o + 1] = pixels[o + 1]; bgra[o + 2] = pixels[o + 2]; bgra[o + 3] = 255;
                    }
                    else if (transparent)
                    {
                        bgra[o] = bgra[o + 1] = bgra[o + 2] = bgra[o + 3] = 0;
                    }
                    else
                    {
                        bgra[o] = Green[0]; bgra[o + 1] = Green[1]; bgra[o + 2] = Green[2]; bgra[o + 3] = 255;
                    }
                }
                return people;
            }
        }
    }
}
