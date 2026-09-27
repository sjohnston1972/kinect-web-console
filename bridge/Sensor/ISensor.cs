using System;

namespace KinectBridge.Sensor
{
    /// <summary>What the sensor is doing right now, from the app's point of view.</summary>
    enum SensorState
    {
        Initialising,
        Ready,
        NoSensor,      // nothing plugged in, or Windows cannot see it
        NotPowered,    // USB is connected but mains power is not reaching the Kinect
        InUse,         // another program has the Kinect open
        BadUsb,        // the USB port cannot carry the Kinect's data
        Error          // anything else the SDK reports
    }

    /// <summary>Tilt motor angle and accelerometer, read together about once a second.</summary>
    class MotionReading
    {
        public int TiltAngle;          // degrees, -27 to +27
        public double X, Y, Z;         // accelerometer, in units of gravity (level and upright is 0, -1, 0)
    }

    /// <summary>
    /// The one shape both the real Kinect and the mock sensor fit, like two SFP modules
    /// for the same port. The rest of the bridge only ever talks to this.
    /// </summary>
    interface ISensor : IDisposable
    {
        bool IsMock { get; }
        SensorState State { get; }

        /// <summary>Extra detail for the page, such as the raw SDK status or the USB id.</summary>
        string Detail { get; }

        /// <summary>Raised whenever State changes. May be raised on any thread.</summary>
        event Action StateChanged;

        /// <summary>Begins looking for the sensor. Returns straight away.</summary>
        void Start();

        /// <summary>Lets go of the sensor and opens it again (the Reconnect button).</summary>
        void Reconnect();

        /// <summary>Reads tilt and accelerometer. Null when there is no working sensor. Talks to USB, so call about once a second.</summary>
        MotionReading ReadMotion();
    }
}
