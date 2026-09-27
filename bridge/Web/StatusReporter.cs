using System;
using System.Collections.Generic;
using System.Threading;
using KinectBridge.Sensor;
using KinectBridge.Skeleton;
using KinectBridge.Streams;

namespace KinectBridge.Web
{
    /// <summary>
    /// Sends the status message: straight away when the sensor state changes,
    /// and once a second anyway so tilt and accelerometer stay current.
    /// </summary>
    class StatusReporter : IDisposable
    {
        const int EveryMs = 1000;

        readonly ISensor sensor;
        readonly MessageHub hub;
        readonly StreamPump pump;
        readonly TiltController tilt;
        readonly SkeletonPump skeletons;
        readonly SkeletonSettings skeletonSettings;
        readonly DateTime started = DateTime.UtcNow;
        Timer timer;
        volatile MotionReading lastMotion;   // cached, so a new browser gets status without a USB read
        volatile Dictionary<string, double> lastRates = new Dictionary<string, double>();

        public StatusReporter(ISensor sensor, MessageHub hub, StreamPump pump, TiltController tilt, SkeletonPump skeletons, SkeletonSettings skeletonSettings)
        {
            this.skeletons = skeletons;
            this.skeletonSettings = skeletonSettings;
            this.sensor = sensor;
            this.hub = hub;
            this.pump = pump;
            this.tilt = tilt;
            hub.CurrentStatus = BuildJson;
            sensor.StateChanged += () => { if (sensor.State != SensorState.Ready) lastMotion = null; Push(); };
        }

        public void Start()
        {
            timer = new Timer(_ => Tick(), null, 0, EveryMs);
        }

        void Tick()
        {
            try
            {
                lastMotion = sensor.ReadMotion();
                var rates = pump.TakeRates();
                var skeletonRate = skeletons.TakeRate();
                if (rates.Count > 0) rates["skeletons"] = skeletonRate;
                lastRates = rates;
                Push();
            }
            catch (Exception ex)
            {
                Log.Error("Status update failed: " + ex.Message);
            }
        }

        /// <summary>Sends status now, rather than waiting for the next second. Used after a setting changes.</summary>
        public void Push()
        {
            hub.Broadcast(BuildJson(), "status");
        }

        string BuildJson()
        {
            var state = sensor.State;
            var help = FaultHelp.For(state, sensor.IsMock);
            var motion = lastMotion;

            return Json.Serialize(new
            {
                type = "status",
                v = MessageHub.ProtocolVersion,
                mock = sensor.IsMock,
                sensor = new
                {
                    state = StateName(state),
                    light = help.Light,
                    title = help.Title,
                    help = help.Help,
                    detail = sensor.Detail
                },
                tilt = motion == null ? null : (object)new { angle = motion.TiltAngle, moving = tilt.IsMoving },
                accelerometer = motion == null ? null : (object)new
                {
                    x = Math.Round(motion.X, 3),
                    y = Math.Round(motion.Y, 3),
                    z = Math.Round(motion.Z, 3),
                    // Angles from gravity: 0 and 0 means the Kinect sits level
                    sideTilt = Math.Round(Degrees(Math.Atan2(motion.X, -motion.Y)), 1),
                    frontTilt = Math.Round(Degrees(Math.Atan2(-motion.Z, -motion.Y)), 1)   // same sign as the tilt motor: up is positive
                },
                fps = lastRates,   // frames per second arriving from the sensor, per stream
                live = new { peopleHighlight = pump.HighlightPeople },
                skeleton = SkeletonSettingsMessage(),
                uptimeSeconds = (int)(DateTime.UtcNow - started).TotalSeconds
            });
        }

        object SkeletonSettingsMessage()
        {
            lock (skeletonSettings) return new { mode = skeletonSettings.ModeName, smoothing = skeletonSettings.SmoothingName };
        }

        static double Degrees(double radians) => radians * 180 / Math.PI;

        /// <summary>The state as the page sees it: ready, noSensor, notPowered and so on.</summary>
        static string StateName(SensorState state)
        {
            var name = state.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        public void Dispose()
        {
            timer?.Dispose();
        }
    }
}
