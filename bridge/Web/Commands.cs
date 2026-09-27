using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KinectBridge.Fusion;

namespace KinectBridge.Web
{
    /// <summary>
    /// What the bridge does for each message a browser can send (docs/PROTOCOL.md lists them).
    /// Each tab's commands live in their own file: Commands.Live.cs, Commands.Skeleton.cs, Commands.Mocap.cs
    /// and Commands.Scan.cs. Every handler checks its inputs and answers in plain English if it cannot help.
    /// </summary>
    static partial class Commands
    {
        public static void Register(AppParts parts)
        {
            RegisterLive(parts);
            RegisterSkeleton(parts);
            RegisterMocap(parts);
            RegisterScan(parts);

            // One "export" message for both takes and scans; it hands over to the right tab's exporter.
            // Exports can take seconds, so they run in the background and this tab's other messages carry on.
            parts.Hub.On("export", (client, msg) =>
            {
                var kind = Text(msg, "kind");
                var format = Text(msg, "format");
                if (kind == "scan")
                {
                    var cleanup = new MeshWriter.Cleanup
                    {
                        RemoveFragments = !(msg.TryGetValue("clean", out var cl) && cl is bool keepAll && !keepAll),
                        RemoveFloor = msg.TryGetValue("removeFloor", out var rf) && rf is bool floor && floor,
                    };
                    Task.Run(() => ExportScan(client, parts.Scanner, format, cleanup));
                }
                else if (kind == "take")
                {
                    ExportTakeRequest(client, parts, Text(msg, "id"), format, Text(msg, "names") == "mixamo");
                }
                else
                {
                    MessageHub.SendError(client, "badExport", "Export needs a kind: take or scan.");
                }
            });
        }

        /// <summary>A text value from a message, or null.</summary>
        static string Text(Dictionary<string, object> msg, string key) =>
            msg.TryGetValue(key, out var value) ? value as string : null;

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

        /// <summary>A true-or-false setting from a message; false (and an error to the browser) if it is missing.</summary>
        static bool TryGetFlag(ClientConnection client, Dictionary<string, object> msg, string type, out bool on)
        {
            on = msg.TryGetValue("on", out var raw) && raw is bool b && b;
            if (raw is bool) return true;
            MessageHub.SendError(client, "badSetting", $"{type} needs on: true or false.");
            return false;
        }
    }
}
