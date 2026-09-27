using System;
using System.IO;

namespace KinectBridge.Streams
{
    /// <summary>
    /// The 9-byte header on every binary WebSocket message (see docs/PROTOCOL.md):
    /// byte 0 is the stream type, bytes 1 to 8 the frame's timestamp in milliseconds, little-endian.
    /// </summary>
    static class StreamHeader
    {
        public const int Length = 9;

        public const byte Colour = 1;
        public const byte DepthView = 2;
        public const byte DepthRaw = 3;
        public const byte FusionPreview = 4;

        public static void Write(Stream stream, byte type, long timestamp)
        {
            stream.WriteByte(type);
            stream.Write(ToLittleEndian(timestamp), 0, 8);
        }

        public static void Write(byte[] buffer, byte type, long timestamp)
        {
            buffer[0] = type;
            Buffer.BlockCopy(ToLittleEndian(timestamp), 0, buffer, 1, 8);
        }

        static byte[] ToLittleEndian(long value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return bytes;
        }
    }
}
