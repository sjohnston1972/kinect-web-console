using System;
using System.Threading;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// A fake Kinect, used with --mock so the app can be built and tested with the Kinect unplugged.
    /// It pretends to start up, then reports ready with a level accelerometer that wobbles slightly.
    /// Test frames and a walking skeleton are added as those streams are built.
    /// </summary>
    class MockSensor : ISensor
    {
        const int StartupMs = 1500;

        readonly DateTime created = DateTime.UtcNow;
        Timer startup;
        volatile SensorState state = SensorState.Initialising;

        public bool IsMock => true;
        public SensorState State => state;
        public string Detail => "Fake sensor (mock mode)";
        public event Action StateChanged;

        public void Start()
        {
            SetState(SensorState.Initialising);
            startup?.Dispose();
            startup = new Timer(_ => SetState(SensorState.Ready), null, StartupMs, Timeout.Infinite);
        }

        public void Reconnect()
        {
            Log.Info("Reconnecting the mock sensor");
            Start();
        }

        public MotionReading ReadMotion()
        {
            if (state != SensorState.Ready) return null;
            // A gentle wobble, so the page visibly updates
            var t = (DateTime.UtcNow - created).TotalSeconds;
            return new MotionReading { TiltAngle = 0, X = 0.01 * Math.Sin(t / 3), Y = -1, Z = 0.01 * Math.Cos(t / 5) };
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
            startup?.Dispose();
        }
    }
}
