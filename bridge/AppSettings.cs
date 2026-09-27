using System;
using System.Collections.Generic;
using System.IO;

namespace KinectBridge
{
    /// <summary>
    /// The contents of settings.json, plus the folders worked out from it.
    /// Missing values fall back to defaults, so a half-edited file still starts the app.
    /// </summary>
    class AppSettings
    {
        public int Port = 8766;
        public string CapturesFolder = "captures";
        public int JpegQuality = 80;

        /// <summary>The "fusion" section, read by FusionPresets. Null if settings.json has none.</summary>
        public Dictionary<string, object> Fusion;

        /// <summary>True for a self-test run, which keeps everything in its own throwaway data folder.</summary>
        public bool IsSelfTest;

        /// <summary>The project folder: the one holding settings.json and web/.</summary>
        public string RootFolder;

        public string WebPath => Path.Combine(RootFolder, "web");
        public string LogsPath => Path.Combine(RootFolder, "logs");
        public string CapturesPath => Path.GetFullPath(Path.Combine(RootFolder, CapturesFolder));

        public static AppSettings Load(string rootFolder)
        {
            var settings = new AppSettings { RootFolder = rootFolder };
            var file = Path.Combine(rootFolder, "settings.json");
            if (!File.Exists(file))
            {
                Log.Warn("settings.json not found, using defaults");
                return settings;
            }

            try
            {
                var data = Json.Parse(File.ReadAllText(file));
                settings.Port = ReadInt(data, "port", settings.Port);
                settings.CapturesFolder = ReadString(data, "capturesFolder", settings.CapturesFolder);
                if (data.TryGetValue("streamQuality", out var q) && q is Dictionary<string, object> quality)
                    settings.JpegQuality = ReadInt(quality, "jpegQuality", settings.JpegQuality);
                if (data.TryGetValue("fusion", out var f) && f is Dictionary<string, object> fusion)
                    settings.Fusion = fusion;
            }
            catch (Exception ex)
            {
                // A typo in settings.json should not stop the app: say so and carry on with defaults
                Log.Error("settings.json could not be read, using defaults: " + ex.Message);
            }
            return settings;
        }

        /// <summary>
        /// Walks up from the program's own folder until it finds the project folder.
        /// This works whether the program runs from bridge\bin or anywhere below the project.
        /// Looks for web\index.html rather than just a "web" folder: Windows ignores letter case,
        /// so the bridge\Web code folder would otherwise be mistaken for it.
        /// </summary>
        public static string FindRootFolder()
        {
            foreach (var start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "settings.json")) ||
                        File.Exists(Path.Combine(dir.FullName, "web", "index.html")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            throw new DirectoryNotFoundException("Could not find the project folder (the one with settings.json and web).");
        }

        static int ReadInt(Dictionary<string, object> data, string key, int fallback)
        {
            return data.TryGetValue(key, out var value) && value is int i ? i : fallback;
        }

        static string ReadString(Dictionary<string, object> data, string key, string fallback)
        {
            return data.TryGetValue(key, out var value) && value is string s && s.Length > 0 ? s : fallback;
        }
    }
}
