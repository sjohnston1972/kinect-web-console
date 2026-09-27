using KinectBridge.Sensor;

namespace KinectBridge.Streams
{
    /// <summary>
    /// Packs depth for the 3D point cloud: every second pixel in each direction (320x240),
    /// each a 16-bit distance in millimetres, little-endian. A quarter of the full size,
    /// which is plenty for a point cloud and keeps the message small (150 KB).
    /// </summary>
    static class RawDepthPacker
    {
        public const int Width = DepthFrame.Width / 2, Height = DepthFrame.Height / 2;

        public static byte[] Pack(DepthFrame frame)
        {
            var message = new byte[StreamHeader.Length + Width * Height * 2];
            StreamHeader.Write(message, StreamHeader.DepthRaw, frame.Timestamp);

            int o = StreamHeader.Length;
            for (int y = 0; y < Height; y++)
            {
                int row = y * 2 * DepthFrame.Width;
                for (int x = 0; x < Width; x++)
                {
                    int mm = frame.Depth[row + x * 2];
                    if (mm < 0) mm = 0;
                    message[o++] = (byte)(mm & 0xFF);
                    message[o++] = (byte)(mm >> 8);
                }
            }
            return message;
        }
    }
}
