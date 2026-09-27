using System;
using KinectBridge.Fusion;

namespace KinectBridge.Web
{
    /// <summary>Commands for the 3D scan tab: start, pause, reset, preset, colour, turntable, and exporting scans.</summary>
    static partial class Commands
    {
        static void RegisterScan(AppParts p)
        {
            p.Hub.On("fusion.start", (client, msg) =>
            {
                var refusal = p.Scanner.Start();
                if (refusal != null) MessageHub.SendError(client, "fusionRefused", refusal);
            });
            p.Hub.On("fusion.pause", (client, msg) => p.Scanner.Pause());
            p.Hub.On("fusion.reset", (client, msg) => p.Scanner.Reset());

            p.Hub.On("fusion.preset", (client, msg) =>
            {
                var name = Text(msg, "preset");
                var refusal = p.Scanner.SetPreset(name);
                if (refusal != null) { MessageHub.SendError(client, "badSetting", refusal); return; }
                p.Prefs.ScanPreset = name;
                p.Prefs.Save();
            });

            p.Hub.On("fusion.turntable", (client, msg) =>
            {
                if (!TryGetFlag(client, msg, "fusion.turntable", out var on)) return;
                p.Scanner.SetTurntable(on);
                p.Prefs.ScanTurntable = on;
                p.Prefs.Save();
            });

            p.Hub.On("fusion.colour", (client, msg) =>
            {
                if (!TryGetFlag(client, msg, "fusion.colour", out var on)) return;
                p.Scanner.SetColour(on);
                p.Prefs.ScanColour = on;
                p.Prefs.Save();
            });
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
    }
}
