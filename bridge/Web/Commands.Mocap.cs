using System;
using System.IO;
using System.Threading.Tasks;
using KinectBridge.Mocap;

namespace KinectBridge.Web
{
    /// <summary>Commands for the Motion capture tab: record, stop, rename, trim, delete, and exporting takes.</summary>
    static partial class Commands
    {
        static void RegisterMocap(AppParts p)
        {
            p.Hub.On("mocap.start", (client, msg) =>
            {
                var refusal = p.Recorder.Start();
                if (refusal != null) MessageHub.SendError(client, "mocapRefused", refusal);
            });

            p.Hub.On("mocap.stop", (client, msg) => p.Recorder.Stop());

            p.Hub.On("mocap.rename", (client, msg) =>
            {
                var id = Text(msg, "id");
                if (!TakeExists(client, p, id)) return;
                try
                {
                    var info = p.Takes.Rename(id, Text(msg, "name"));
                    Log.Info($"Renamed take {id} to {info.Id}");
                    p.Recorder.Broadcast(new { kind = "renamed", oldId = id, id = info.Id, name = info.Name }, includeTakes: true);
                }
                catch (ArgumentException ex) { MessageHub.SendError(client, "badName", ex.Message); }
            });

            p.Hub.On("mocap.trim", (client, msg) =>
            {
                var id = Text(msg, "id");
                if (!TakeExists(client, p, id)) return;
                double? start = TryGetNumber(msg, "start", out var s) ? s : (double?)null;
                double? end = TryGetNumber(msg, "end", out var e) ? e : (double?)null;
                try
                {
                    var info = p.Takes.SetTrim(id, start, end);
                    Log.Info(start == null && end == null ? $"Cleared the trim on take {id}" : $"Trimmed take {id} to {info.TrimStart:0.0} to {info.TrimEnd:0.0} s");
                    p.Recorder.Broadcast(new { kind = "trimmed", id, trimStart = info.TrimStart, trimEnd = info.TrimEnd }, includeTakes: true);
                }
                catch (ArgumentException ex) { MessageHub.SendError(client, "badTrim", ex.Message); }
            });

            p.Hub.On("mocap.delete", (client, msg) =>
            {
                var id = Text(msg, "id");
                if (!TakeExists(client, p, id)) return;
                p.Takes.Delete(id);
                Log.Info($"Deleted take {id} (moved to the Recycle Bin)");
                p.Recorder.Broadcast(new { kind = "deleted", id }, includeTakes: true);
            });
        }

        /// <summary>True if the take exists; otherwise tells the browser and refreshes its take list.</summary>
        static bool TakeExists(ClientConnection client, AppParts p, string id)
        {
            if (p.Takes.Exists(id)) return true;
            MessageHub.SendError(client, "noSuchTake", "That take no longer exists. The list has been refreshed.");
            p.Recorder.Broadcast(null, includeTakes: true);
            return false;
        }

        /// <summary>An export request for a take: JSON is already on disk; BVH is made in the background.</summary>
        static void ExportTakeRequest(ClientConnection client, AppParts p, string id, string format, bool mixamo)
        {
            if (!p.Takes.Exists(id)) { MessageHub.SendError(client, "noSuchTake", "That take no longer exists."); return; }
            if (format == "json")
            {
                MessageHub.Send(client, new { type = "export", v = MessageHub.ProtocolVersion, kind = "take", id, format, name = id + ".json", url = TakeUrl(id + ".json") });
                return;
            }
            if (format != "bvh") { MessageHub.SendError(client, "badExport", "Takes export as bvh or json."); return; }
            Task.Run(() => ExportTake(client, p.Takes, id, mixamo));
        }

        /// <summary>Makes the BVH file for a take and replies with its download link. Runs in the background.</summary>
        static void ExportTake(ClientConnection client, TakeLibrary takes, string id, bool mixamo)
        {
            try
            {
                var path = mixamo ? takes.MixamoBvhPath(id) : takes.BvhPath(id);
                var name = Path.GetFileName(path);
                var result = BvhExporter.Export(takes.Load(id), path, mixamo);
                var note = $"{result.Frames} frames at 30 per second. Joints land on average {result.AverageErrorCm:0.0} cm from where the Kinect saw them."
                    + (result.LiftedFrames > 0 ? $" {result.LiftedFrames} frames were lifted so the feet stay on the floor." : "")
                    + (result.PeopleInTake > 1 ? $" The take has {result.PeopleInTake} people; the BVH holds the one tracked longest." : "");
                Log.Info($"Exported {name}: {note}");
                MessageHub.Send(client, new { type = "export", v = MessageHub.ProtocolVersion, kind = "take", id, format = "bvh", names = mixamo ? "mixamo" : "kinect", name, url = TakeUrl(name), note });
            }
            catch (InvalidOperationException ex) { MessageHub.SendError(client, "exportFailed", ex.Message); }
            catch (Exception ex)
            {
                Log.Error($"BVH export of {id} failed: {ex.Message}");
                MessageHub.SendError(client, "exportFailed", "The BVH file could not be made. The bridge log has details.");
            }
        }

        static string TakeUrl(string fileName) => "/captures/mocap/" + Uri.EscapeDataString(fileName);
    }
}
