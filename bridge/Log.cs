using System;
using System.Collections.Generic;
using System.IO;

namespace KinectBridge
{
    class LogLine
    {
        public DateTime Time;
        public string Level;
        public string Text;
    }

    /// <summary>
    /// The bridge log. Each line goes to three places: the console window, logs\bridge.log,
    /// and anyone listening to LineAdded (the web page's log viewer).
    /// Keeps the newest 50 lines in memory so a freshly opened page can show recent history.
    /// </summary>
    static class Log
    {
        const int KeepInMemory = 50;
        const long MaxFileBytes = 1024 * 1024;   // roll to a new file after 1 MB
        const int OldFilesToKeep = 3;            // bridge.1.log to bridge.3.log

        static readonly object Gate = new object();
        static readonly Queue<LogLine> Recent = new Queue<LogLine>();
        static string filePath;

        public static event Action<LogLine> LineAdded;

        public static void Init(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                filePath = Path.Combine(folder, "bridge.log");
            }
            catch (Exception ex)
            {
                // Logging to file is a nice-to-have: keep running with console and page logging only
                Console.WriteLine("Could not open log folder: " + ex.Message);
            }
        }

        public static void Info(string text) { Write("info", text); }
        public static void Warn(string text) { Write("warn", text); }
        public static void Error(string text) { Write("error", text); }

        public static List<LogLine> RecentLines()
        {
            lock (Gate) return new List<LogLine>(Recent);
        }

        static void Write(string level, string text)
        {
            var line = new LogLine { Time = DateTime.Now, Level = level, Text = text };
            var formatted = $"{line.Time:yyyy-MM-dd HH:mm:ss} {level.ToUpperInvariant(),-5} {text}";

            lock (Gate)
            {
                Recent.Enqueue(line);
                while (Recent.Count > KeepInMemory) Recent.Dequeue();

                Console.WriteLine(formatted);
                AppendToFile(formatted);
            }

            // Outside the lock, so a slow listener cannot hold up logging elsewhere
            try { LineAdded?.Invoke(line); }
            catch { /* the page's log viewer must never break logging */ }
        }

        static void AppendToFile(string formatted)
        {
            if (filePath == null) return;
            try
            {
                var info = new FileInfo(filePath);
                if (info.Exists && info.Length > MaxFileBytes) RollFiles();
                File.AppendAllText(filePath, formatted + Environment.NewLine);
            }
            catch
            {
                // Disk full or file locked: skip the file, the console and page still get the line
            }
        }

        /// <summary>bridge.log becomes bridge.1.log, 1 becomes 2, and so on. The oldest is deleted.</summary>
        static void RollFiles()
        {
            var folder = Path.GetDirectoryName(filePath);
            string Old(int n) => Path.Combine(folder, $"bridge.{n}.log");

            if (File.Exists(Old(OldFilesToKeep))) File.Delete(Old(OldFilesToKeep));
            for (int n = OldFilesToKeep - 1; n >= 1; n--)
                if (File.Exists(Old(n))) File.Move(Old(n), Old(n + 1));
            File.Move(filePath, Old(1));
        }
    }
}
