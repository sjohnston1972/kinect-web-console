using System;
using System.Diagnostics;
using System.Threading;
using KinectBridge.Skeleton;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// A fake Kinect, used with --mock so the app can be built and tested with the Kinect unplugged.
    /// It pretends to start up, then sends 30 colour and depth frames a second of a simple room
    /// (MockScene draws them), and has a pretend tilt motor that the accelerometer follows.
    /// Skeletons for the two pretend people come from MockSkeleton.
    /// </summary>
    class MockSensor : ISensor
    {
        const int StartupMs = 1500;
        const double FrameMs = 1000.0 / 30;
        const int TiltMs = 600;   // a real move takes about this long

        readonly DateTime created = DateTime.UtcNow;
        readonly MockScene scene = new MockScene();
        readonly MockSkeleton skeletons = new MockSkeleton();
        volatile SkeletonSettings skeletonSettings = new SkeletonSettings();
        Timer startup;
        Thread frameThread;
        volatile bool stopping;
        volatile SensorState state = SensorState.Initialising;
        volatile int tiltAngle;

        public bool IsMock => true;
        public SensorState State => state;
        public string Detail => "Fake sensor (mock mode)";
        public event Action StateChanged;
        public event Action<ColourFrame> ColourFrameReady;
        public event Action<DepthFrame> DepthFrameReady;
        public event Action<SkeletonData> SkeletonFrameReady;

        /// <summary>The mock's colour and depth pictures are drawn from the same viewpoint, so pixel i matches pixel i.</summary>
        public bool MapDepthToColour(DepthFrame depth, int[] colourIndex)
        {
            if (state != SensorState.Ready) return false;
            for (int i = 0; i < colourIndex.Length; i++) colourIndex[i] = i;
            return true;
        }

        public void ApplySkeletonSettings(SkeletonSettings settings)
        {
            skeletonSettings = settings.Copy();
        }

        public void Start()
        {
            SetState(SensorState.Initialising);
            startup?.Dispose();
            startup = new Timer(_ => SetState(SensorState.Ready), null, StartupMs, Timeout.Infinite);

            if (frameThread == null)
            {
                frameThread = new Thread(FrameLoop) { IsBackground = true, Name = "Mock frames" };
                frameThread.Start();
            }
        }

        public void Reconnect()
        {
            Log.Info("Reconnecting the mock sensor");
            Start();
        }

        public MotionReading ReadMotion()
        {
            if (state != SensorState.Ready) return null;
            // Gravity as the accelerometer would see it at this tilt, plus a gentle wobble so the page visibly updates
            var t = (DateTime.UtcNow - created).TotalSeconds;
            var radians = tiltAngle * Math.PI / 180;
            return new MotionReading
            {
                TiltAngle = tiltAngle,
                X = 0.01 * Math.Sin(t / 3),
                Y = -Math.Cos(radians),
                Z = -Math.Sin(radians)
            };
        }

        public void SetTilt(int angle)
        {
            if (state != SensorState.Ready) throw new InvalidOperationException("The mock sensor is not ready");
            Thread.Sleep(TiltMs);
            tiltAngle = angle;
        }

        /// <summary>Sends frames at 30 a second while Ready, timed off a stopwatch so they do not drift.</summary>
        void FrameLoop()
        {
            var clock = Stopwatch.StartNew();
            long frameNumber = 0;
            while (!stopping)
            {
                var due = (long)(++frameNumber * FrameMs);
                var wait = due - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -500) frameNumber = (long)(clock.ElapsedMilliseconds / FrameMs);   // fell far behind: skip ahead

                if (state != SensorState.Ready) continue;
                try
                {
                    var seconds = clock.Elapsed.TotalSeconds;

                    var colour = ColourFrame.Rent();
                    scene.DrawColour(colour.Pixels, seconds);
                    Hand(ColourFrameReady, colour);

                    var depth = DepthFrame.Rent();
                    scene.DrawDepth(depth.Depth, depth.Player, seconds);
                    Hand(DepthFrameReady, depth);

                    SkeletonFrameReady?.Invoke(skeletons.Build(seconds, skeletonSettings));
                }
                catch (Exception ex)
                {
                    Log.Error("Mock frame failed: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        static void Hand<T>(Action<T> listeners, T frame) where T : PooledFrame
        {
            if (listeners == null) { frame.Release(); return; }
            listeners(frame);
        }

        void SetState(SensorState newState)
        {
            if (state == newState) return;
            state = newState;
            Log.Info($"Mock sensor is now {newState}");
            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            stopping = true;
            startup?.Dispose();
            scene.Dispose();
        }
    }
}
