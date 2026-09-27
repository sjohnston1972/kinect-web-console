using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace KinectBridge.Streams
{
    /// <summary>
    /// Turns raw 4-bytes-per-pixel pictures into JPEG (for streaming) or PNG (for snapshots),
    /// using the image tools built into Windows.
    /// Each streaming worker has its own JpegEncoder, so the reusable picture buffer is never shared between threads.
    /// </summary>
    class JpegEncoder : IDisposable
    {
        static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        readonly Bitmap bitmap;
        readonly EncoderParameters quality;

        public JpegEncoder(int width, int height, int jpegQuality)
        {
            bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Max(10, Math.Min(100, jpegQuality)));
        }

        /// <summary>Returns a complete binary message: the stream header followed by the JPEG.</summary>
        public byte[] Encode(byte[] bgra, byte streamType, long timestamp)
        {
            ImageCopy.Into(bitmap, bgra);
            using (var output = new MemoryStream(96 * 1024))
            {
                StreamHeader.Write(output, streamType, timestamp);
                bitmap.Save(output, JpegCodec, quality);
                return output.ToArray();
            }
        }

        public void Dispose()
        {
            bitmap.Dispose();
            quality.Dispose();
        }
    }

    static class ImageCopy
    {
        /// <summary>Copies raw pixels into a bitmap of the same size.</summary>
        public static void Into(Bitmap bitmap, byte[] bgra)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(bgra, 0, data.Scan0, bitmap.Width * bitmap.Height * 4); }
            finally { bitmap.UnlockBits(data); }
        }

        public static void SavePng(byte[] bgra, int width, int height, string path, bool withTransparency = false)
        {
            var format = withTransparency ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppRgb;
            using (var bitmap = new Bitmap(width, height, format))
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, format);
                try { Marshal.Copy(bgra, 0, data.Scan0, width * height * 4); }
                finally { bitmap.UnlockBits(data); }
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}
