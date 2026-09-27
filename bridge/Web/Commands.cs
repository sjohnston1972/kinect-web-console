using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KinectBridge.Fusion;
using KinectBridge.Mocap;
using KinectBridge.Sensor;
using KinectBridge.Skeleton;
using KinectBridge.Streams;

namespace KinectBridge.Web
{
    /// <summary>
    /// What the bridge does for each message a browser can send.
    /// Each handler checks its inputs and answers in plain English if it cannot help.
    /// </summary>
    static class Commands
    {
        public static void Register(MessageHub hub, ISensor sensor, TiltController tilt, StreamPump pump,
            SkeletonSettings skeletonSettings, Action pushStatus, Recorder recorder, TakeLibrary takes, Scanner scanner, Preferences prefs,
            SkeletonPump skeletons)
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

            hub.On("tilt.autoframe", (client, msg) =>
            {
                var current = sensor.ReadMotion()?.TiltAngle;
                if (current == null) { MessageHub.SendError(client, "tiltRefused", "The Kinect is not ready, so it cannot tilt."); return; }
                var target = AutoFrame.Target(skeletons.Latest, current.Value, out var note);
                if (target != null)
                {
                    var refusal = tilt.Request(target.Value);
                    if (refusal != null) { MessageHub.SendError(client, "tiltRefused", refusal); return; }
                }
                MessageHub.Send(client, new { type = "autoframe", v = MessageHub.ProtocolVersion, angle = target, note });
            });

            hub.On("live.settings", (client, msg) =>
            {
                if (msg.TryGetValue("peopleHighlight", out var value) && value is bool on)
                {
                    pump.HighlightPeople = on;
                    prefs.PeopleHighlight = on;
                    prefs.Save();
                    Log.Info($"People highlight {(on ? "on" : "off")}");
                }
            });

            hub.On("skeleton.settings", (client, msg) =>
            {
                SkeletonSettings updated;
                lock (skeletonSettings)
                {
                    if (msg.TryGetValue("mode", out var m))
                    {
                        if (!SkeletonSettings.TryParseMode(m as string, out var mode))
                        {
                            MessageHub.SendError(client, "badSetting", "Tracking mode must be standing or seated.");
                            return;
                        }
                        skeletonSettings.Mode = mode;
                    }
                    if (msg.TryGetValue("smoothing", out var s))
                    {
                        if (!SkeletonSettings.TryParseSmoothing(s as string, out var smoothing))
                        {
                            MessageHub.SendError(client, "badSetting", "Smoothing must be off, light or heavy.");
                            return;
                        }
                        skeletonSettings.Smoothing = smoothing;
                    }
                    updated = skeletonSettings.Copy();
                }
                sensor.ApplySkeletonSettings(updated);
                prefs.SkeletonMode = updated.ModeName;
                prefs.SkeletonSmoothing = updated.SmoothingName;
                prefs.Save();
                Log.Info($"Skeleton tracking: {updated.ModeName}, smoothing {updated.SmoothingName}");
                pushStatus();
            });

            hub.On("mocap.start", (client, msg) =>
            {
                var refusal = recorder.Start();
                if (refusal != null) MessageHub.SendError(client, "mocapRefused", refusal);
            });

            hub.On("mocap.stop", (client, msg) => recorder.Stop());

            hub.On("mocap.rename", (client, msg) =>
            {
                var id = msg.TryGetValue("id", out var i) ? i as string : null;
                var name = msg.TryGetValue("name", out var n) ? n as string : null;
                if (!takes.Exists(id)) { MessageHub.SendError(client, "noSuchTake", "That take no longer exists. The list has been refreshed."); recorder.Broadcast(null, includeTakes: true); return; }
                try
                {
                    var info = takes.Rename(id, name);
                    Log.Info($"Renamed take {id} to {info.Id}");
                    recorder.Broadcast(new { kind = "renamed", oldId = id, id = info.Id, name = info.Name }, includeTakes: true);
                }
                catch (ArgumentException ex) { MessageHub.SendError(client, "badName", ex.Message); }
            });

            hub.On("mocap.trim", (client, msg) =>
            {
                var id = msg.TryGetValue("id", out var i) ? i as string : null;
                if (!takes.Exists(id)) { MessageHub.SendError(client, "noSuchTake", "That take no longer exists. The list has been refreshed."); recorder.Broadcast(null, includeTakes: true); return; }
                double? start = TryGetNumber(msg, "start", out var s) ? s : (double?)null;
                double? end = TryGetNumber(msg, "end", out var e) ? e : (double?)null;
                try
                {
                    var info = takes.SetTrim(id, start, end);
                    Log.Info(start == null && end == null ? $"Cleared the trim on take {id}" : $"Trimmed take {id} to {info.TrimStart:0.0} to {info.TrimEnd:0.0} s");
                    recorder.Broadcast(new { kind = "trimmed", id, trimStart = info.TrimStart, trimEnd = info.TrimEnd }, includeTakes: true);
                }
                catch (ArgumentException ex) { MessageHub.SendError(client, "badTrim", ex.Message); }
            });

            hub.On("mocap.delete", (client, msg) =>
            {
                var id = msg.TryGetValue("id", out var i) ? i as string : null;
                if (!takes.Exists(id)) { MessageHub.SendError(client, "noSuchTake", "That take no longer exists. The list has been refreshed."); recorder.Broadcast(null, includeTakes: true); return; }
                takes.Delete(id);
                Log.Info($"Deleted take {id} (moved to the Recycle Bin)");
                recorder.Broadcast(new { kind = "deleted", id }, includeTakes: true);
            });

            hub.On("export", (client, msg) =>
            {
                var kind = msg.TryGetValue("kind", out var k) ? k as string : null;
                var id = msg.TryGetValue("id", out var i) ? i as string : null;
                var format = msg.TryGetValue("format", out var f) ? f as string : null;
                // Exports can take seconds, so they run in the background; this tab's other messages carry on meanwhile
                if (kind == "scan")
                {
                    var cleanup = new MeshWriter.Cleanup
                    {
                        RemoveFragments = !(msg.TryGetValue("clean", out var cl) && cl is bool keepAll && !keepAll),
                        RemoveFloor = msg.TryGetValue("removeFloor", out var rf) && rf is bool floor && floor,
                    };
                    Task.Run(() => ExportScan(client, scanner, format, cleanup));
                    return;
                }
                if (kind != "take") { MessageHub.SendError(client, "badExport", "Export needs a kind: take or scan."); return; }
                if (!takes.Exists(id)) { MessageHub.SendError(client, "noSuchTake", "That take no longer exists."); return; }

                if (format == "json")
                {
                    MessageHub.Send(client, new { type = "export", v = MessageHub.ProtocolVersion, kind, id, format, name = id + ".json", url = TakeUrl(id + ".json") });
                    return;
                }
                if (format != "bvh") { MessageHub.SendError(client, "badExport", "Takes export as bvh or json."); return; }
                var mixamo = msg.TryGetValue("names", out var n) && n as string == "mixamo";
                Task.Run(() => ExportTake(client, takes, id, mixamo));
            });

            hub.On("fusion.start", (client, msg) =>
            {
                var refusal = scanner.Start();
                if (refusal != null) MessageHub.SendError(client, "fusionRefused", refusal);
            });
            hub.On("fusion.pause", (client, msg) => scanner.Pause());
            hub.On("fusion.reset", (client, msg) => scanner.Reset());
            hub.On("fusion.preset", (client, msg) =>
            {
                var name = msg.TryGetValue("preset", out var p) ? p as string : null;
                var refusal = scanner.SetPreset(name);
                if (refusal != null) { MessageHub.SendError(client, "badSetting", refusal); return; }
                prefs.ScanPreset = name;
                prefs.Save();
            });
            hub.On("fusion.turntable", (client, msg) =>
            {
                if (msg.TryGetValue("on", out var on) && on is bool b)
                {
                    scanner.SetTurntable(b);
                    prefs.ScanTurntable = b;
                    prefs.Save();
                }
                else MessageHub.SendError(client, "badSetting", "fusion.turntable needs on: true or false.");
            });
            hub.On("fusion.colour", (client, msg) =>
            {
                if (msg.TryGetValue("on", out var on) && on is bool b)
                {
                    scanner.SetColour(b);
                    prefs.ScanColour = b;
                    prefs.Save();
                }
                else MessageHub.SendError(client, "badSetting", "fusion.colour needs on: true or false.");
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

        /// <summary>Makes the mesh file and replies with its download link. Runs in the background; scanning only waits while the mesh is read out.</summary>
        static void ExportScan(ClientConnection client, Scanner scanner, string format, MeshWriter.Cleanup cleanup)
        {
            if (format != "stl" && format != "obj" && format != "ply" && format != "preview")
            {
                MessageHub.SendError(client, "badExport", "Scans export as stl, obj or ply.");
                return;
            }
            try
            {
                var (name, url, result) = scanner.Export(format, cleanup);
                var note = $"{result.Triangles:N0} triangles, {result.SizeX:0.00} m wide, {result.SizeY:0.00} m deep, {result.SizeZ:0.00} m tall."
                    + (result.RemovedPieces > 0 ? $" Removed {result.RemovedPieces:N0} small floating pieces ({result.RemovedPieceTriangles:N0} triangles)." : "")
                    + (result.RemovedFloorTriangles > 0 ? $" Removed the floor ({result.RemovedFloorTriangles:N0} triangles)." : cleanup.RemoveFloor ? " No level floor was found to remove." : "");
                MessageHub.Send(client, new { type = "export", v = MessageHub.ProtocolVersion, kind = "scan", format, name, url, note });
            }
            catch (InvalidOperationException ex) { MessageHub.SendError(client, "exportFailed", ex.Message); }
            catch (Exception ex)
            {
                Log.Error($"Scan export ({format}) failed: {ex.Message}");
                MessageHub.SendError(client, "exportFailed", "The scan could not be saved. Check there is space on the disk; the bridge log has details.");
            }
        }

        static string TakeUrl(string fileName) => "/captures/mocap/" + Uri.EscapeDataString(fileName);

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
