using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KinectBridge.Sensor;
using KinectBridge.Skeleton;
using KinectBridge.Web;

namespace KinectBridge.Mocap
{
    /// <summary>
    /// Records motion capture takes: a 3-second countdown to get into position, then every skeleton frame
    /// (not just the newest, as the live view uses) until Stop is pressed. The take is then saved as JSON.
    /// Sends "mocap" messages to every page with the state, countdown, and take list.
    /// </summary>
    class Recorder : IDisposable
    {
        const int CountdownSeconds = 3;
        const int MaxSeconds = 600;          // stop by itself after 10 minutes, so memory cannot run away
        const int UpdateEveryMs = 250;

        enum State { Idle, Countdown, Recording, Saving }

        class Frame
        {
            public long T;                  // milliseconds since recording started
            public List<Body> Bodies;
        }

        readonly ISensor sensor;
        readonly SkeletonPump skeletons;
        readonly MessageHub hub;
        readonly TakeLibrary library;
        readonly SkeletonSettings skeletonSettings;
        readonly object gate = new object();
        readonly Timer ticker;
        State state = State.Idle;
        DateTime countdownEnds;
        long startedAt;                     // Unix milliseconds
        List<Frame> frames = new List<Frame>();
        float[] floor;
        int peopleNow;

        public Recorder(ISensor sensor, SkeletonPump skeletons, MessageHub hub, TakeLibrary library, SkeletonSettings skeletonSettings)
        {
            this.sensor = sensor;
            this.skeletons = skeletons;
            this.hub = hub;
            this.library = library;
            this.skeletonSettings = skeletonSettings;
            // From the skeleton pump rather than the sensor, so frames arrive with their person numbers
            skeletons.FrameReady += OnSkeleton;
            ticker = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Starts the countdown. Returns null, or a plain-English reason it cannot start.</summary>
        public string Start()
        {
            lock (gate)
            {
                if (state != State.Idle) return "A take is already being recorded or saved.";
                if (sensor.State != SensorState.Ready) return "The Kinect is not ready, so there is nothing to record. The status light says why.";
                state = State.Countdown;
                countdownEnds = DateTime.UtcNow.AddSeconds(CountdownSeconds);
                ticker.Change(0, UpdateEveryMs);
            }
            Log.Info("Motion capture: countdown started");
            return null;
        }

        /// <summary>Stops recording and saves, or cancels a countdown.</summary>
        public void Stop()
        {
            List<Frame> recorded;
            lock (gate)
            {
                if (state == State.Countdown)
                {
                    state = State.Idle;
                    ticker.Change(Timeout.Infinite, Timeout.Infinite);
                    Log.Info("Motion capture: countdown cancelled");
                    Broadcast(null);
                    return;
                }
                if (state != State.Recording) return;
                state = State.Saving;
                recorded = frames;
                frames = new List<Frame>();
            }
            Broadcast(null);
            Task.Run(() => Save(recorded));
        }

        void Tick()
        {
            bool autoStop = false;
            lock (gate)
            {
                if (state == State.Countdown && DateTime.UtcNow >= countdownEnds)
                {
                    state = State.Recording;
                    startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    frames = new List<Frame>();
                    floor = null;
                    Log.Info("Motion capture: recording");
                }
                if (state == State.Recording && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt > MaxSeconds * 1000L)
                    autoStop = true;
                if (state == State.Idle || state == State.Saving) ticker.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (autoStop)
            {
                Log.Warn($"Motion capture: stopped by itself after {MaxSeconds / 60} minutes");
                Stop();
                return;
            }
            Broadcast(null);
        }

        void OnSkeleton(SkeletonData data)
        {
            lock (gate)
            {
                peopleNow = data.Bodies.Count;
                if (state != State.Recording) return;
                frames.Add(new Frame { T = data.Timestamp - startedAt, Bodies = data.Bodies });
                if (floor == null && data.Floor != null) floor = data.Floor;
            }
        }

        void Save(List<Frame> recorded)
        {
            object outcome;
            try
            {
                var people = recorded.SelectMany(f => f.Bodies).Select(b => b.Id).Distinct().Count();
                if (people == 0)
                {
                    outcome = new { kind = "failed", message = "Nobody was tracked during that take, so it was not saved. Stand in view (the Skeleton tab shows when you are tracked) and try again." };
                    Log.Warn("Motion capture: nobody was tracked, take not saved");
                }
                else
                {
                    var created = DateTime.Now;
                    var take = BuildTake(recorded, created, people);
                    var info = library.Save(take, "take-" + created.ToString("yyyyMMdd-HHmmss"));
                    outcome = new { kind = "saved", id = info.Id, name = info.Name };
                    Log.Info($"Motion capture: saved {info.Id} ({info.Duration:0.0} s, {info.Frames} frames, {people} {(people == 1 ? "person" : "people")})");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Motion capture: saving failed: " + ex.Message);
                outcome = new { kind = "failed", message = "The take could not be saved. Check there is space on the disk and the captures folder is not read-only." };
            }
            lock (gate) state = State.Idle;
            Broadcast(outcome, includeTakes: true);
        }

        /// <summary>The take file's contents. The format is described in docs/PROTOCOL.md.</summary>
        Dictionary<string, object> BuildTake(List<Frame> recorded, DateTime created, int people)
        {
            SkeletonSettings settings;
            lock (skeletonSettings) settings = skeletonSettings.Copy();
            var duration = recorded.Count > 0 ? recorded[recorded.Count - 1].T / 1000.0 : 0;

            return new Dictionary<string, object>
            {
                ["format"] = TakeFormat.Name,
                ["version"] = TakeFormat.Version,
                ["name"] = "Take " + created.ToString("yyyy-MM-dd HH:mm:ss"),
                ["created"] = created.ToString("yyyy-MM-dd HH:mm:ss"),
                ["mode"] = settings.ModeName,
                ["smoothing"] = settings.SmoothingName,
                ["durationSeconds"] = Math.Round(duration, 3),
                ["frameCount"] = recorded.Count,
                ["people"] = people,
                ["floor"] = floor,
                ["frames"] = recorded.Select(f => new Dictionary<string, object>
                {
                    ["t"] = f.T,
                    ["bodies"] = f.Bodies.Select(ToRecord).ToList(),
                }).ToList(),
            };
        }

        static object ToRecord(Body body)
        {
            var joints = new Dictionary<string, object>();
            for (int j = 0; j < Joints.Count; j++)
            {
                var joint = body.Joints[j];
                if (joint == null) continue;
                var r = joint.Rotation;
                joints[Joints.Names[j]] = new Dictionary<string, object>
                {
                    ["p"] = new[] { Math.Round(joint.X, 4), Math.Round(joint.Y, 4), Math.Round(joint.Z, 4) },
                    ["state"] = joint.Inferred ? "inferred" : "tracked",
                    ["rot"] = new[] { Math.Round(r.X, 5), Math.Round(r.Y, 5), Math.Round(r.Z, 5), Math.Round(r.W, 5) },
                };
            }
            return new Dictionary<string, object> { ["id"] = body.Id, ["player"] = body.Player, ["person"] = body.Person, ["joints"] = joints };
        }

        /// <summary>The current mocap message. Takes are included on connect and whenever the list changes.</summary>
        public string Message(bool includeTakes = true, object outcome = null)
        {
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var message = new Dictionary<string, object>
                {
                    ["type"] = "mocap",
                    ["v"] = MessageHub.ProtocolVersion,
                    ["state"] = state.ToString().ToLowerInvariant(),
                    ["countdown"] = state == State.Countdown ? (int)Math.Ceiling((countdownEnds - DateTime.UtcNow).TotalSeconds) : 0,
                    ["elapsed"] = state == State.Recording ? Math.Round((now - startedAt) / 1000.0, 1) : 0,
                    ["frames"] = state == State.Recording ? frames.Count : 0,
                    ["peopleNow"] = peopleNow,
                };
                if (includeTakes)
                    message["takes"] = library.List().Select(t => new { id = t.Id, name = t.Name, created = t.Created, duration = t.Duration, frames = t.Frames, people = t.People, mode = t.Mode }).ToList();
                if (outcome != null) message["event"] = outcome;
                return Json.Serialize(message);
            }
        }

        /// <summary>Sends to every page. State updates are newest-only; ones with news (a save, a new list) always arrive.</summary>
        public void Broadcast(object outcome, bool includeTakes = false)
        {
            var important = outcome != null || includeTakes;
            if (important) hub.Broadcast(Message(includeTakes, outcome));
            // Always refresh the newest-only state too, so an older unsent state can never arrive after the news
            hub.Broadcast(Message(false), "mocap");
        }

        public void Dispose()
        {
            skeletons.FrameReady -= OnSkeleton;
            ticker.Dispose();
        }
    }
}
