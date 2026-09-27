using System;
using System.Collections.Generic;
using System.Linq;

namespace KinectBridge.Fusion
{
    /// <summary>
    /// One 3D scanning set-up: how big the scanned space is, how fine its detail, and where it sits.
    /// Kinect Fusion builds the model inside a box of voxels (tiny cubes): more voxels per metre means finer
    /// detail, and voxels times size sets how much graphics memory it needs.
    /// </summary>
    class ScanPreset
    {
        public string Name;              // object, person or room
        public string Label;
        public float VoxelsPerMeter;
        public int VoxelsX, VoxelsY, VoxelsZ;
        public float StartDistance;      // metres from the Kinect to the front of the box
        public float MinDepth, MaxDepth; // depth readings outside this range are ignored, in metres
        public int MeshVoxelStep;        // 1 = full detail when exporting, 2 = every other voxel (a quarter of the triangles)
        public bool OnFloor;             // stand the box on the floor (when the Kinect can see it) rather than centring it on the Kinect's view

        public double SizeX => VoxelsX / VoxelsPerMeter;
        public double SizeY => VoxelsY / VoxelsPerMeter;
        public double SizeZ => VoxelsZ / VoxelsPerMeter;
        public double VoxelMm => 1000 / VoxelsPerMeter;
    }

    /// <summary>
    /// The presets, from the "fusion" section of settings.json, falling back to built-in values
    /// for anything missing. The built-in values match the tuned ones shipped in settings.json.
    /// </summary>
    static class ScanPresets
    {
        public static readonly string[] Order = { "object", "person", "room" };

        static readonly Dictionary<string, ScanPreset> Defaults = new Dictionary<string, ScanPreset>
        {
            // A 1 m box, 2 mm detail, starting 0.6 m away, centred on where the Kinect points: a box, a bag, a seat
            ["object"] = new ScanPreset { Name = "object", Label = "Object", VoxelsPerMeter = 512, VoxelsX = 512, VoxelsY = 512, VoxelsZ = 512, StartDistance = 0.6f, MinDepth = 0.5f, MaxDepth = 1.8f, MeshVoxelStep = 1 },
            // 1.5 m wide, 2 m tall, standing on the floor, 4 mm detail, starting 1 m away: a person, or a chair
            ["person"] = new ScanPreset { Name = "person", Label = "Person", VoxelsPerMeter = 256, VoxelsX = 384, VoxelsY = 512, VoxelsZ = 384, StartDistance = 1.0f, MinDepth = 0.5f, MaxDepth = 3.0f, MeshVoxelStep = 1, OnFloor = true },
            // 4 m by 3 m by 4 m, 8 mm detail: a corner of a room
            ["room"] = new ScanPreset { Name = "room", Label = "Room", VoxelsPerMeter = 128, VoxelsX = 512, VoxelsY = 384, VoxelsZ = 512, StartDistance = 0.5f, MinDepth = 0.5f, MaxDepth = 6.0f, MeshVoxelStep = 2 },
        };

        public static Dictionary<string, ScanPreset> Load(Dictionary<string, object> fusionSection)
        {
            var result = new Dictionary<string, ScanPreset>();
            Dictionary<string, object> configured = null;
            if (fusionSection != null && fusionSection.TryGetValue("presets", out var p)) configured = p as Dictionary<string, object>;

            foreach (var name in Order)
            {
                var d = Defaults[name];
                var preset = new ScanPreset
                {
                    Name = name, Label = d.Label, VoxelsPerMeter = d.VoxelsPerMeter,
                    VoxelsX = d.VoxelsX, VoxelsY = d.VoxelsY, VoxelsZ = d.VoxelsZ,
                    StartDistance = d.StartDistance, MinDepth = d.MinDepth, MaxDepth = d.MaxDepth, MeshVoxelStep = d.MeshVoxelStep, OnFloor = d.OnFloor,
                };
                if (configured != null && configured.TryGetValue(name, out var raw) && raw is Dictionary<string, object> c)
                {
                    try
                    {
                        if (c.TryGetValue("label", out var l) && l is string label) preset.Label = label;
                        if (c.TryGetValue("voxelsPerMeter", out var v)) preset.VoxelsPerMeter = Convert.ToSingle(v);
                        if (c.TryGetValue("voxels", out var vx) && vx is object[] sizes && sizes.Length == 3)
                        {
                            // Fusion needs each side to be a multiple of 32 voxels
                            preset.VoxelsX = RoundTo32(sizes[0]); preset.VoxelsY = RoundTo32(sizes[1]); preset.VoxelsZ = RoundTo32(sizes[2]);
                        }
                        if (c.TryGetValue("startDistance", out var s)) preset.StartDistance = Convert.ToSingle(s);
                        if (c.TryGetValue("minDepth", out var mn)) preset.MinDepth = Convert.ToSingle(mn);
                        if (c.TryGetValue("maxDepth", out var mx)) preset.MaxDepth = Convert.ToSingle(mx);
                        if (c.TryGetValue("meshVoxelStep", out var st)) preset.MeshVoxelStep = Math.Max(1, Convert.ToInt32(st));
                        if (c.TryGetValue("onFloor", out var fl) && fl is bool onFloor) preset.OnFloor = onFloor;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"settings.json: the {name} scan preset has a value that is not a number ({ex.Message}); using built-in values for the rest");
                    }
                }
                result[name] = preset;
            }
            return result;
        }

        static int RoundTo32(object value) => Math.Max(32, (int)Math.Round(Convert.ToDouble(value) / 32) * 32);
    }
}
