using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// A frame that goes back into a pool for reuse when everyone has finished with it.
    ///
    /// Why pool: each colour frame is 1.2 MB and arrives 30 times a second. Allocating fresh
    /// memory every time makes .NET stop and tidy up often, which shows as stutter.
    /// Anyone who keeps a frame calls AddRef, and Release when done; the last Release returns it.
    /// </summary>
    abstract class PooledFrame
    {
        int refs;

        /// <summary>When the frame arrived, in milliseconds since 1 January 1970 (UTC).</summary>
        public long Timestamp;

        public void AddRef() { Interlocked.Increment(ref refs); }

        public void Release()
        {
            if (Interlocked.Decrement(ref refs) == 0) ReturnToPool();
        }

        protected void ResetForRent()
        {
            refs = 1;
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        protected abstract void ReturnToPool();
    }

    /// <summary>One 640x480 colour picture, 4 bytes per pixel in blue, green, red, unused order.</summary>
    class ColourFrame : PooledFrame
    {
        public const int Width = 640, Height = 480;
        const int KeepSpare = 6;
        static readonly ConcurrentBag<ColourFrame> Pool = new ConcurrentBag<ColourFrame>();

        public readonly byte[] Pixels = new byte[Width * Height * 4];

        public static ColourFrame Rent()
        {
            if (!Pool.TryTake(out var frame)) frame = new ColourFrame();
            frame.ResetForRent();
            return frame;
        }

        protected override void ReturnToPool()
        {
            if (Pool.Count < KeepSpare) Pool.Add(this);
        }
    }

    /// <summary>
    /// One 640x480 depth picture: distance in millimetres per pixel (0 means no reading),
    /// and which tracked person (1 to 6) each pixel belongs to, or 0 for nobody.
    /// </summary>
    class DepthFrame : PooledFrame
    {
        public const int Width = 640, Height = 480;
        public const int MinDepth = 800, MaxDepth = 4000;   // the Xbox 360 Kinect's reliable range, in mm
        const int KeepSpare = 6;
        static readonly ConcurrentBag<DepthFrame> Pool = new ConcurrentBag<DepthFrame>();

        public readonly short[] Depth = new short[Width * Height];
        public readonly byte[] Player = new byte[Width * Height];

        public static DepthFrame Rent()
        {
            if (!Pool.TryTake(out var frame)) frame = new DepthFrame();
            frame.ResetForRent();
            return frame;
        }

        protected override void ReturnToPool()
        {
            if (Pool.Count < KeepSpare) Pool.Add(this);
        }
    }
}
