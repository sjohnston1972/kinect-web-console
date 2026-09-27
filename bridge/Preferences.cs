using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace KinectBridge
{
    /// <summary>
    /// The choices made on the page, remembered between runs: skeleton tracking mode and smoothing,
    /// people highlight, and the 3D scan preset and colour setting.
    ///
    /// Kept in preferences.json, separate from settings.json: settings.json is for editing by hand and the
    /// app never rewrites it, while preferences.json is written by the app whenever a choice changes.
    /// A self-test run keeps its own copy in its throwaway data folder.
    /// </summary>
    class Preferences
    {
        public string SkeletonMode = "standing";
        public string SkeletonSmoothing = "light";
        public bool PeopleHighlight;
        public string ScanPreset = "object";
        public bool ScanColour;
        public bool ScanTurntable;

        const int SaveDelayMs = 500;   // several quick changes are saved once
        readonly object gate = new object();
        string path;
        Timer saveTimer;

        public static Preferences Load(string path)
        {
            var prefs = new Preferences { path = path };
            if (!File.Exists(path)) return prefs;
            try
            {
                var data = Json.Parse(File.ReadAllText(path));
                prefs.SkeletonMode = Text(data, "skeletonMode", prefs.SkeletonMode);
                prefs.SkeletonSmoothing = Text(data, "skeletonSmoothing", prefs.SkeletonSmoothing);
                prefs.PeopleHighlight = Flag(data, "peopleHighlight", prefs.PeopleHighlight);
                prefs.ScanPreset = Text(data, "scanPreset", prefs.ScanPreset);
                prefs.ScanColour = Flag(data, "scanColour", prefs.ScanColour);
                prefs.ScanTurntable = Flag(data, "scanTurntable", prefs.ScanTurntable);
            }
            catch (Exception ex)
            {
                // A damaged file just means starting from the defaults; it is rewritten on the next change
                Log.Warn("preferences.json could not be read, using defaults: " + ex.Message);
            }
            return prefs;
        }

        /// <summary>Saves shortly after the last change, off the calling thread.</summary>
        public void Save()
        {
            lock (gate)
            {
                if (saveTimer == null) saveTimer = new Timer(_ => WriteNow(), null, Timeout.Infinite, Timeout.Infinite);
                saveTimer.Change(SaveDelayMs, Timeout.Infinite);
            }
        }

        void WriteNow()
        {
            try
            {
                Dictionary<string, object> data;
                lock (gate)
                {
                    data = new Dictionary<string, object>
                    {
                        ["skeletonMode"] = SkeletonMode,
                        ["skeletonSmoothing"] = SkeletonSmoothing,
                        ["peopleHighlight"] = PeopleHighlight,
                        ["scanPreset"] = ScanPreset,
                        ["scanColour"] = ScanColour,
                        ["scanTurntable"] = ScanTurntable,
                    };
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".saving";
                File.WriteAllText(temp, Json.SerializeIndented(data) + Environment.NewLine);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not save preferences.json: " + ex.Message);
            }
        }

        static string Text(Dictionary<string, object> d, string key, string fallback) =>
            d.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : fallback;

        static bool Flag(Dictionary<string, object> d, string key, bool fallback) =>
            d.TryGetValue(key, out var v) && v is bool b ? b : fallback;
    }
}
