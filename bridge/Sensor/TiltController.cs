using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KinectBridge.Sensor
{
    /// <summary>
    /// Guards the tilt motor. Microsoft's rule for the Kinect: no more than one move per second,
    /// and no more than 15 moves in any 20 seconds, or the motor can overheat and wear.
    /// Requests that break the rule are refused (not queued), with a message saying when to try again.
    /// </summary>
    class TiltController
    {
        public const int MinAngle = -27, MaxAngle = 27;
        static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);
        static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
        const int MaxMovesInWindow = 15;

        readonly ISensor sensor;
        readonly object gate = new object();
        readonly Queue<DateTime> recentMoves = new Queue<DateTime>();
        bool moving;

        public TiltController(ISensor sensor)
        {
            this.sensor = sensor;
        }

        public bool IsMoving { get { lock (gate) return moving; } }

        /// <summary>
        /// Starts a move if the rules allow it. Returns null if accepted, or a plain-English reason if refused.
        /// The move itself runs in the background, because the motor takes a moment.
        /// </summary>
        public string Request(int angle)
        {
            angle = Math.Max(MinAngle, Math.Min(MaxAngle, angle));
            var now = DateTime.UtcNow;

            lock (gate)
            {
                if (sensor.State != SensorState.Ready) return "The Kinect is not ready, so it cannot tilt right now.";
                if (moving) return "The Kinect is still tilting. Try again when it stops.";

                while (recentMoves.Count > 0 && now - recentMoves.Peek() > Window) recentMoves.Dequeue();

                if (recentMoves.Count > 0)
                {
                    var sinceLast = now - LastMove();
                    if (sinceLast < MinGap)
                        return $"Tilt is limited to one move per second to protect the motor. Try again in {(MinGap - sinceLast).TotalSeconds:0.0} s.";
                }
                if (recentMoves.Count >= MaxMovesInWindow)
                {
                    var wait = Window - (now - recentMoves.Peek());
                    return $"Tilt is limited to 15 moves in 20 seconds to protect the motor. Try again in {Math.Ceiling(wait.TotalSeconds)} s.";
                }

                recentMoves.Enqueue(now);
                moving = true;
            }

            Task.Run(() => Move(angle));
            return null;
        }

        void Move(int angle)
        {
            try
            {
                Log.Info($"Tilting to {angle} degrees");
                sensor.SetTilt(angle);
            }
            catch (Exception ex)
            {
                Log.Warn("Tilt failed: " + ex.Message);
            }
            finally
            {
                lock (gate) moving = false;
            }
        }

        DateTime LastMove()
        {
            var last = DateTime.MinValue;
            foreach (var t in recentMoves) last = t;
            return last;
        }
    }
}
