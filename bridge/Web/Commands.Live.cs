using System;
using System.Linq;
using KinectBridge.Skeleton;

namespace KinectBridge.Web
{
    /// <summary>Commands for the Live tab and the header: reconnect, tilt, Fit, people highlight and snapshots.</summary>
    static partial class Commands
    {
        static void RegisterLive(AppParts p)
        {
            p.Hub.On("sensor.reconnect", (client, msg) => p.Sensor.Reconnect());

            p.Hub.On("tilt", (client, msg) =>
            {
                if (!TryGetNumber(msg, "angle", out var angle))
                {
                    MessageHub.SendError(client, "badTilt", "The tilt message needs an angle in degrees.");
                    return;
                }
                var refusal = p.Tilt.Request((int)Math.Round(angle));
                if (refusal != null) MessageHub.SendError(client, "tiltRefused", refusal);
            });

            p.Hub.On("tilt.autoframe", (client, msg) =>
            {
                var current = p.Sensor.ReadMotion()?.TiltAngle;
                if (current == null) { MessageHub.SendError(client, "tiltRefused", "The Kinect is not ready, so it cannot tilt."); return; }
                var target = AutoFrame.Target(p.Skeletons.Latest, current.Value, out var note);
                if (target != null)
                {
                    var refusal = p.Tilt.Request(target.Value);
                    if (refusal != null) { MessageHub.SendError(client, "tiltRefused", refusal); return; }
                }
                MessageHub.Send(client, new { type = "autoframe", v = MessageHub.ProtocolVersion, angle = target, note });
            });

            p.Hub.On("live.settings", (client, msg) =>
            {
                if (msg.TryGetValue("peopleHighlight", out var value) && value is bool on)
                {
                    p.Pump.HighlightPeople = on;
                    p.Prefs.PeopleHighlight = on;
                    p.Prefs.Save();
                    Log.Info($"People highlight {(on ? "on" : "off")}");
                }
            });

            p.Hub.On("snapshot", (client, msg) =>
            {
                try
                {
                    var names = p.Pump.SaveSnapshot(cutout: msg.TryGetValue("cutout", out var cut) && cut is bool withCutout && withCutout);
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
    }
}
