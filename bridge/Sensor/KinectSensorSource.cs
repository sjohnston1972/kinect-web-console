using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Kinect;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// The real Xbox 360 Kinect, through Microsoft's SDK v1.8.
    ///
    /// Recovery works two ways, like link-state events plus a keepalive:
    /// the SDK raises StatusChanged when the Kinect is plugged or unplugged, and a timer
    /// re-checks every 2 seconds anyway. The timer catches cases with no event, such as
    /// another program closing and freeing the Kinect.
    /// </summary>
    class KinectSensorSource : ISensor
    {
        const int CheckEveryMs = 2000;

        readonly object gate = new object();
        KinectSensor active;          // the sensor we have open, or null
        Timer watchdog;
        SensorState state = SensorState.Initialising;
        string detail = "";

        public bool IsMock => false;
        public SensorState State { get { lock (gate) return state; } }
        public string Detail { get { lock (gate) return detail; } }
        public event Action StateChanged;
        public event Action<ColourFrame> ColourFrameReady;
        public event Action<DepthFrame> DepthFrameReady;

        public void Start()
        {
            KinectSensor.KinectSensors.StatusChanged += OnStatusChanged;
            watchdog = new Timer(_ => Check(), null, 0, CheckEveryMs);
        }

        public void Reconnect()
        {
            Log.Info("Reconnecting to the Kinect");
            lock (gate) Release();
            Check();
        }

        public MotionReading ReadMotion()
        {
            lock (gate)
            {
                if (active == null || !active.IsRunning) return null;
                try
                {
                    var a = active.AccelerometerGetCurrentReading();
                    return new MotionReading { TiltAngle = active.ElevationAngle, X = a.X, Y = a.Y, Z = a.Z };
                }
                catch (Exception ex)
                {
                    // Usually means the Kinect was just unplugged: the next Check() sorts out the state
                    Log.Warn("Could not read tilt and accelerometer: " + ex.Message);
                    return null;
                }
            }
        }

        public void SetTilt(int angle)
        {
            KinectSensor sensor;
            lock (gate) sensor = active;
            if (sensor == null || !sensor.IsRunning) throw new InvalidOperationException("No working Kinect");
            // Blocks while the motor moves. Done outside the gate so status reads carry on meanwhile.
            sensor.ElevationAngle = angle;
        }

        void OnColourFrame(object sender, ColorImageFrameReadyEventArgs e)
        {
            using (var image = e.OpenColorImageFrame())
            {
                if (image == null) return;   // the SDK skipped this one: normal under load
                var frame = ColourFrame.Rent();
                image.CopyPixelDataTo(frame.Pixels);
                Hand(ColourFrameReady, frame);
            }
        }

        void OnDepthFrame(object sender, DepthImageFrameReadyEventArgs e)
        {
            using (var image = e.OpenDepthImageFrame())
            {
                if (image == null) return;
                var pixels = depthScratch;
                if (pixels == null || pixels.Length != image.PixelDataLength)
                    pixels = depthScratch = new DepthImagePixel[image.PixelDataLength];
                image.CopyDepthImagePixelDataTo(pixels);

                var frame = DepthFrame.Rent();
                for (int i = 0; i < pixels.Length; i++)
                {
                    frame.Depth[i] = pixels[i].Depth;
                    frame.Player[i] = (byte)pixels[i].PlayerIndex;
                }
                Hand(DepthFrameReady, frame);
            }
        }

        DepthImagePixel[] depthScratch;   // reused every frame; the SDK raises depth frames on one thread

        /// <summary>Passes a frame on. If nobody is listening, it goes straight back to the pool.</summary>
        static void Hand<T>(Action<T> listeners, T frame) where T : PooledFrame
        {
            if (listeners == null) { frame.Release(); return; }
            try { listeners(frame); }
            catch (Exception ex) { Log.Error("Frame handling failed: " + ex.Message); }
        }

        void OnStatusChanged(object sender, StatusChangedEventArgs e)
        {
            Log.Info($"Kinect reported status {e.Status}");
            Check();
        }

        /// <summary>
        /// Looks at what the SDK can see and gets us to the right state:
        /// keep the open sensor if it is healthy, otherwise let it go and try to open one.
        /// </summary>
        void Check()
        {
            try
            {
                lock (gate)
                {
                    if (active != null)
                    {
                        if (active.Status == KinectStatus.Connected && active.IsRunning)
                        {
                            SetState(SensorState.Ready, active.DeviceConnectionId);
                            return;
                        }
                        Log.Warn($"Lost the Kinect (status {active.Status})");
                        Release();
                    }

                    var sensors = KinectSensor.KinectSensors.ToList();
                    if (sensors.Count == 0)
                    {
                        SetState(SensorState.NoSensor, "Windows cannot see a Kinect");
                        return;
                    }

                    var candidate = sensors.FirstOrDefault(s => s.Status == KinectStatus.Connected);
                    if (candidate == null)
                    {
                        var status = sensors[0].Status;
                        SetState(FromSdkStatus(status), "SDK status: " + status);
                        return;
                    }

                    Open(candidate);
                }
            }
            catch (Exception ex)
            {
                // The watchdog must keep running whatever happens, or recovery stops
                Log.Error("Sensor check failed: " + ex.Message);
            }
        }

        /// <summary>Called with the gate held.</summary>
        void Open(KinectSensor sensor)
        {
            // While another program holds the Kinect we retry every 2 seconds.
            // Stay on "in use" during those retries, so the light does not flicker amber and red.
            if (state != SensorState.InUse)
                SetState(SensorState.Initialising, sensor.DeviceConnectionId);
            try
            {
                // Colour and depth at 640x480, 30 frames a second. Skeleton tracking is switched on too:
                // without it the SDK does not mark which depth pixels belong to a person.
                sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
                sensor.DepthStream.Enable(DepthImageFormat.Resolution640x480Fps30);
                sensor.SkeletonStream.Enable();
                sensor.ColorFrameReady += OnColourFrame;
                sensor.DepthFrameReady += OnDepthFrame;

                sensor.Start();
                active = sensor;
                SetState(SensorState.Ready, sensor.DeviceConnectionId);
            }
            catch (IOException)
            {
                // The SDK's way of saying another process already has the Kinect open
                Unhook(sensor);
                SetState(SensorState.InUse, sensor.DeviceConnectionId);
            }
            catch (Exception ex)
            {
                Unhook(sensor);
                SetState(SensorState.Error, ex.Message);
            }
        }

        /// <summary>Called with the gate held.</summary>
        void Release()
        {
            if (active == null) return;
            Unhook(active);
            try { active.Stop(); } catch { /* already gone */ }
            active = null;
        }

        void Unhook(KinectSensor sensor)
        {
            sensor.ColorFrameReady -= OnColourFrame;
            sensor.DepthFrameReady -= OnDepthFrame;
        }

        static SensorState FromSdkStatus(KinectStatus status)
        {
            switch (status)
            {
                case KinectStatus.Disconnected: return SensorState.NoSensor;
                case KinectStatus.NotPowered: return SensorState.NotPowered;
                case KinectStatus.Initializing: return SensorState.Initialising;
                case KinectStatus.InsufficientBandwidth: return SensorState.BadUsb;
                default: return SensorState.Error;
            }
        }

        /// <summary>Called with the gate held. Logs and raises StateChanged only on a real change.</summary>
        void SetState(SensorState newState, string newDetail)
        {
            if (newState == state && newDetail == detail) return;
            var changedState = newState != state;
            state = newState;
            detail = newDetail;
            if (changedState) Log.Info($"Sensor is now {newState} ({newDetail})");
            ThreadPool.QueueUserWorkItem(_ => StateChanged?.Invoke());
        }

        public void Dispose()
        {
            KinectSensor.KinectSensors.StatusChanged -= OnStatusChanged;
            watchdog?.Dispose();
            lock (gate) Release();
        }
    }
}
