using System;
using System.Collections.Generic;
using System.Linq;
using KinectBridge.Sensor;
using KinectBridge.Streams;

namespace KinectBridge.Web
{
    /// <summary>
    /// What the bridge does for each message a browser can send.
    /// Each handler checks its inputs and answers in plain English if it cannot help.
    /// </summary>
    static class Commands
    {
        public static void Register(MessageHub hub, ISensor sensor, TiltController tilt, StreamPump pump)
        {
            hub.On("sensor.reconnect", (client, msg) => sensor.Reconnect());

            hub.On("tilt", (client, msg) =>
            {
                if (!TryGetNumber(msg, "angle", out var angle))
                {
                    MessageHub.SendError(client, "badTilt", "The tilt message needs an angle in degrees.");
                    return;
                }
                var refusal = tilt.Request((int)Math.Round(angle));
                if (refusal != null) MessageHub.SendError(client, "tiltRefused", refusal);
            });

            hub.On("live.settings", (client, msg) =>
            {
                if (msg.TryGetValue("peopleHighlight", out var value) && value is bool on)
                {
                    pump.HighlightPeople = on;
                    Log.Info($"People highlight {(on ? "on" : "off")}");
                }
            });

            hub.On("snapshot", (client, msg) =>
            {
                try
                {
                    var names = pump.SaveSnapshot();
                    MessageHub.Send(client, new
                    {
                        type = "snapshot",
                        v = MessageHub.ProtocolVersion,
                        files = names.Select(n => new { name = n, url = "/captures/snapshots/" + Uri.EscapeDataString(n) }).ToArray()
                    });
                }
                catch (InvalidOperationException ex)
                {
                    MessageHub.SendError(client, "snapshotFailed", ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Error("Snapshot failed: " + ex.Message);
                    MessageHub.SendError(client, "snapshotFailed", "The snapshot could not be saved. Check there is space on the disk and the captures folder is not read-only.");
                }
            });
        }

        /// <summary>JSON numbers arrive as int or decimal depending on whether they have a fraction.</summary>
        static bool TryGetNumber(Dictionary<string, object> msg, string key, out double value)
        {
            value = 0;
            if (!msg.TryGetValue(key, out var raw)) return false;
            if (raw is int i) { value = i; return true; }
            if (raw is decimal d) { value = (double)d; return true; }
            if (raw is double f) { value = f; return true; }
            return false;
        }
    }
}
