using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using KinectBridge.Sensor;
using KinectBridge.Web;

namespace KinectBridge.Streams
{
    /// <summary>
    /// Takes frames from the sensor and sends them to the browsers that asked for them.
    ///
    /// Only the newest colour frame and newest depth frame are kept. Each stream has its own worker
    /// that encodes the newest frame and sends it; if frames arrive faster than a worker can encode,
    /// the in-between ones are simply never sent. Nothing is encoded for a stream nobody is watching.
    /// </summary>
    class StreamPump : IDisposable
    {
        readonly ISensor sensor;
        readonly MessageHub hub;
        readonly AppSettings settings;
        readonly object gate = new object();
        ColourFrame lastColour;
        DepthFrame lastDepth;

        readonly StreamWorker colourWorker, depthWorker, rawWorker, cutoutWorker;
        readonly JpegEncoder colourJpeg, depthJpeg, cutoutJpeg;
        readonly byte[] depthPicture = new byte[DepthFrame.Width * DepthFrame.Height * 4];   // depth worker only
        readonly byte[] cutoutPicture = new byte[ColourFrame.Width * ColourFrame.Height * 4]; // cut-out worker only
        readonly CutoutMaker cutouts;

        int colourCount, depthCount;
        readonly Stopwatch rateClock = Stopwatch.StartNew();

        /// <summary>Dims everything except tracked people in the depth view and snapshots.</summary>
        public volatile bool HighlightPeople;

        public StreamPump(ISensor sensor, MessageHub hub, AppSettings settings)
        {
            this.sensor = sensor;
            this.hub = hub;
            this.settings = settings;
            colourJpeg = new JpegEncoder(ColourFrame.Width, ColourFrame.Height, settings.JpegQuality);
            depthJpeg = new JpegEncoder(DepthFrame.Width, DepthFrame.Height, settings.JpegQuality);
            cutoutJpeg = new JpegEncoder(ColourFrame.Width, ColourFrame.Height, settings.JpegQuality);
            cutouts = new CutoutMaker(sensor);

            colourWorker = new StreamWorker("Colour stream", SendColour);
            depthWorker = new StreamWorker("Depth stream", SendDepthView);
            rawWorker = new StreamWorker("Raw depth stream", SendDepthRaw);
            cutoutWorker = new StreamWorker("Cut-out stream", SendCutout);

            sensor.ColourFrameReady += OnColour;
            sensor.DepthFrameReady += OnDepth;
            sensor.StateChanged += () => { if (sensor.State != SensorState.Ready) ForgetFrames(); };
        }

        void OnColour(ColourFrame frame)
        {
            Interlocked.Increment(ref colourCount);
            ColourFrame old;
            lock (gate) { old = lastColour; lastColour = frame; }
            old?.Release();
            if (hub.AnySubscribed("colour")) colourWorker.Signal();
            if (hub.AnySubscribed("cutout")) cutoutWorker.Signal();   // paced by the colour picture
        }

        void OnDepth(DepthFrame frame)
        {
            Interlocked.Increment(ref depthCount);
            DepthFrame old;
            lock (gate) { old = lastDepth; lastDepth = frame; }
            old?.Release();
            if (hub.AnySubscribed("depth")) depthWorker.Signal();
            if (hub.AnySubscribed("depthRaw")) rawWorker.Signal();
            DepthArrived?.Invoke();
        }

        void SendColour()
        {
            var frame = Borrow(ref lastColour);
            if (frame == null) return;
            try { hub.BroadcastBinary("colour", colourJpeg.Encode(frame.Pixels, StreamHeader.Colour, frame.Timestamp)); }
            finally { frame.Release(); }
        }

        void SendDepthView()
        {
            var frame = Borrow(ref lastDepth);
            if (frame == null) return;
            try
            {
                DepthColouriser.Paint(frame, depthPicture, HighlightPeople);
                hub.BroadcastBinary("depth", depthJpeg.Encode(depthPicture, StreamHeader.DepthView, frame.Timestamp));
            }
            finally { frame.Release(); }
        }

        /// <summary>The colour picture with only the people kept, on green (the virtual green screen).</summary>
        void SendCutout()
        {
            var colour = Borrow(ref lastColour);
            var depth = Borrow(ref lastDepth);
            try
            {
                if (colour == null || depth == null) return;
                if (cutouts.Make(colour, depth, cutoutPicture, transparent: false) < 0) return;
                hub.BroadcastBinary("cutout", cutoutJpeg.Encode(cutoutPicture, StreamHeader.Cutout, colour.Timestamp));
            }
            finally
            {
                colour?.Release();
                depth?.Release();
            }
        }

        void SendDepthRaw()
        {
            var frame = Borrow(ref lastDepth);
            if (frame == null) return;
            try { hub.BroadcastBinary("depthRaw", RawDepthPacker.Pack(frame)); }
            finally { frame.Release(); }
        }

        /// <summary>Raised on the sensor thread after a new depth frame is stored. The 3D scanner listens for this.</summary>
        public event Action DepthArrived;

        /// <summary>The newest colour frame, counted so it stays valid: call Release when done. Null if none.</summary>
        public ColourFrame BorrowColour() => Borrow(ref lastColour);

        /// <summary>The newest depth frame, counted so it stays valid: call Release when done. Null if none.</summary>
        public DepthFrame BorrowDepth() => Borrow(ref lastDepth);

        /// <summary>Takes a counted reference to the newest frame, so it cannot be recycled while in use.</summary>
        T Borrow<T>(ref T slot) where T : PooledFrame
        {
            lock (gate)
            {
                slot?.AddRef();
                return slot;
            }
        }

        void ForgetFrames()
        {
            ColourFrame c;
            DepthFrame d;
            lock (gate)
            {
                c = lastColour; lastColour = null;
                d = lastDepth; lastDepth = null;
            }
            c?.Release();
            d?.Release();
        }

        /// <summary>
        /// Saves the newest colour and depth pictures as PNG files in captures\snapshots, and with cutout,
        /// the people on a transparent background too. Returns the file names, or throws with a plain-English message.
        /// </summary>
        public List<string> SaveSnapshot(bool cutout = false)
        {
            var colour = Borrow(ref lastColour);
            var depth = Borrow(ref lastDepth);
            try
            {
                if (colour == null || depth == null)
                    throw new InvalidOperationException("There is no picture to save yet: the Kinect is not sending frames.");

                var folder = Path.Combine(settings.CapturesPath, "snapshots");
                Directory.CreateDirectory(folder);
                // Down to the millisecond, so two snapshots in the same second never overwrite each other
                var stem = "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

                var colourName = stem + "-colour.png";
                ImageCopy.SavePng(colour.Pixels, ColourFrame.Width, ColourFrame.Height, Path.Combine(folder, colourName));

                // Its own buffer: the depth worker may be using depthPicture right now
                var depthBgra = new byte[DepthFrame.Width * DepthFrame.Height * 4];
                DepthColouriser.Paint(depth, depthBgra, HighlightPeople);
                var depthName = stem + "-depth.png";
                ImageCopy.SavePng(depthBgra, DepthFrame.Width, DepthFrame.Height, Path.Combine(folder, depthName));

                var names = new List<string> { colourName, depthName };
                if (cutout)
                {
                    var cutoutBgra = new byte[ColourFrame.Width * ColourFrame.Height * 4];
                    if (cutouts.Make(colour, depth, cutoutBgra, transparent: true) > 0)
                    {
                        var cutoutName = stem + "-cutout.png";
                        ImageCopy.SavePng(cutoutBgra, ColourFrame.Width, ColourFrame.Height, Path.Combine(folder, cutoutName), withTransparency: true);
                        names.Add(cutoutName);
                    }
                }

                Log.Info("Snapshot saved: " + string.Join(", ", names));
                return names;
            }
            finally
            {
                colour?.Release();
                depth?.Release();
            }
        }

        /// <summary>Frames per second arriving from the sensor since the last call. Called once a second.</summary>
        public Dictionary<string, double> TakeRates()
        {
            var seconds = rateClock.Elapsed.TotalSeconds;
            rateClock.Restart();
            var colour = Interlocked.Exchange(ref colourCount, 0);
            var depth = Interlocked.Exchange(ref depthCount, 0);
            if (seconds <= 0 || sensor.State != SensorState.Ready) return new Dictionary<string, double>();
            return new Dictionary<string, double>
            {
                { "colour", Math.Round(colour / seconds, 1) },
                { "depth", Math.Round(depth / seconds, 1) },
            };
        }

        public void Dispose()
        {
            sensor.ColourFrameReady -= OnColour;
            sensor.DepthFrameReady -= OnDepth;
            colourWorker.Dispose();
            cutoutWorker.Dispose();
            depthWorker.Dispose();
            rawWorker.Dispose();
            ForgetFrames();
        }
    }
}
