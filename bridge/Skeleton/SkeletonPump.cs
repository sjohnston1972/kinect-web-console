using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using KinectBridge.Sensor;
using KinectBridge.Streams;
using KinectBridge.Web;

namespace KinectBridge.Skeleton
{
    /// <summary>
    /// Sends skeleton frames to the browsers subscribed to "skeletons", as JSON (see docs/PROTOCOL.md).
    /// Same newest-only rule as the pictures: a worker turns the newest frame into JSON,
    /// and frames that arrive while it is busy are skipped.
    /// Also keeps the newest frame for anything else that wants it (motion capture, from Phase 4).
    /// </summary>
    class SkeletonPump : IDisposable
    {
        readonly ISensor sensor;
        readonly MessageHub hub;
        readonly StreamWorker worker;
        SkeletonData latest;
        int count;
        readonly Stopwatch rateClock = Stopwatch.StartNew();

        public SkeletonPump(ISensor sensor, MessageHub hub)
        {
            this.sensor = sensor;
            this.hub = hub;
            worker = new StreamWorker("Skeleton stream", Send);
            sensor.SkeletonFrameReady += OnFrame;
        }

        /// <summary>The newest skeleton frame, or null.</summary>
        public SkeletonData Latest => Volatile.Read(ref latest);

        void OnFrame(SkeletonData data)
        {
            Interlocked.Increment(ref count);
            Volatile.Write(ref latest, data);
            if (hub.AnySubscribed("skeletons")) worker.Signal();
        }

        void Send()
        {
            var data = Latest;
            if (data == null) return;
            hub.BroadcastToSubscribers("skeletons", Json.Serialize(ToMessage(data)));
        }

        /// <summary>
        /// The skeletons message. Positions are rounded to the millimetre and pixels to a tenth,
        /// which keeps the message small without losing anything the Kinect can actually measure.
        /// </summary>
        static object ToMessage(SkeletonData data)
        {
            var bodies = new List<object>();
            foreach (var body in data.Bodies)
            {
                var joints = new Dictionary<string, object>();
                for (int j = 0; j < Joints.Count; j++)
                {
                    var joint = body.Joints[j];
                    if (joint == null) continue;
                    joints[Joints.Names[j]] = new
                    {
                        p = new[] { R(joint.X, 3), R(joint.Y, 3), R(joint.Z, 3) },
                        colour = new[] { R(joint.ColourX, 1), R(joint.ColourY, 1) },
                        depth = new[] { R(joint.DepthX, 1), R(joint.DepthY, 1) },
                        state = joint.Inferred ? "inferred" : "tracked",
                    };
                }
                bodies.Add(new { id = body.Id, player = body.Player, joints });
            }

            return new
            {
                type = "skeletons",
                v = MessageHub.ProtocolVersion,
                timestamp = data.Timestamp,
                floor = data.Floor,
                bodies,
            };
        }

        static double R(float value, int places) => Math.Round(value, places);

        /// <summary>Skeleton frames per second from the sensor since the last call.</summary>
        public double TakeRate()
        {
            var seconds = rateClock.Elapsed.TotalSeconds;
            rateClock.Restart();
            var frames = Interlocked.Exchange(ref count, 0);
            return seconds > 0 ? Math.Round(frames / seconds, 1) : 0;
        }

        public void Dispose()
        {
            sensor.SkeletonFrameReady -= OnFrame;
            worker.Dispose();
        }
    }
}
