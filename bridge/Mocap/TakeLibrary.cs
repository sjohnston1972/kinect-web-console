using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace KinectBridge.Mocap
{
    /// <summary>What the take list shows about one take, without loading all its frames.</summary>
    class TakeInfo
    {
        public string Id;          // the file name without .json; also how the page refers to the take
        public string Name;        // the name shown on the page
        public string Created;     // local date and time, "yyyy-MM-dd HH:mm:ss"
        public double Duration;    // seconds
        public int Frames;
        public int People;
        public string Mode;        // standing or seated
    }

    /// <summary>
    /// The takes saved in captures\mocap, one JSON file each (format in docs/PROTOCOL.md).
    /// Keeps a list in memory so the page's take list is instant, and only ever touches files it
    /// has listed itself, so a message from the page can never reach a file outside the folder.
    /// Deleted takes go to the Recycle Bin, so a mistake can be undone.
    /// </summary>
    class TakeLibrary
    {
        readonly string folder;
        readonly bool useRecycleBin;
        readonly object gate = new object();
        readonly Dictionary<string, TakeInfo> takes = new Dictionary<string, TakeInfo>(StringComparer.OrdinalIgnoreCase);

        /// <param name="useRecycleBin">False for self-test runs, whose throwaway takes should not fill the Recycle Bin.</param>
        public TakeLibrary(string folder, bool useRecycleBin = true)
        {
            this.folder = folder;
            this.useRecycleBin = useRecycleBin;
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    var info = ReadInfo(Path.GetFileNameWithoutExtension(file), Json.Parse(File.ReadAllText(file)));
                    takes[info.Id] = info;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipped {Path.GetFileName(file)}: it is not a take this app can read ({ex.Message})");
                }
            }
            Log.Info($"Found {takes.Count} saved takes");
        }

        /// <summary>Newest first.</summary>
        public List<TakeInfo> List()
        {
            lock (gate) return takes.Values.OrderByDescending(t => t.Created).ToList();
        }

        public bool Exists(string id)
        {
            lock (gate) return id != null && takes.ContainsKey(id);
        }

        public string JsonPath(string id) => Path.Combine(folder, id + ".json");
        public string BvhPath(string id) => Path.Combine(folder, id + ".bvh");

        /// <summary>Saves a new take. Returns its details.</summary>
        public TakeInfo Save(Dictionary<string, object> take, string fileStem)
        {
            lock (gate)
            {
                var id = UniqueId(fileStem, null);
                WriteAtomically(JsonPath(id), Json.Serialize(take));
                var info = ReadInfo(id, take);
                takes[id] = info;
                return info;
            }
        }

        public Dictionary<string, object> Load(string id)
        {
            if (!Exists(id)) throw new FileNotFoundException("That take no longer exists.");
            return Json.Parse(File.ReadAllText(JsonPath(id)));
        }

        /// <summary>Gives a take a new name. The file is renamed to match, so it is easy to find in File Explorer.</summary>
        public TakeInfo Rename(string id, string newName)
        {
            newName = (newName ?? "").Trim();
            if (newName.Length == 0) throw new ArgumentException("The new name is empty.");
            if (newName.Length > 80) newName = newName.Substring(0, 80);

            lock (gate)
            {
                if (!takes.ContainsKey(id)) throw new FileNotFoundException("That take no longer exists.");
                var take = Json.Parse(File.ReadAllText(JsonPath(id)));
                take["name"] = newName;

                var newId = UniqueId(SafeFileName(newName), id);
                WriteAtomically(JsonPath(newId), Json.Serialize(take));
                if (!string.Equals(newId, id, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(JsonPath(id));
                    if (File.Exists(BvhPath(id))) File.Delete(BvhPath(id));   // exported under the old name; export again
                    takes.Remove(id);
                }
                var info = ReadInfo(newId, take);
                takes[newId] = info;
                return info;
            }
        }

        public void Delete(string id)
        {
            lock (gate)
            {
                if (!takes.ContainsKey(id)) throw new FileNotFoundException("That take no longer exists.");
                foreach (var path in new[] { JsonPath(id), BvhPath(id) })
                    if (File.Exists(path))
                    {
                        if (useRecycleBin) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        else File.Delete(path);
                    }
                takes.Remove(id);
            }
        }

        static TakeInfo ReadInfo(string id, Dictionary<string, object> take)
        {
            if (!(take.TryGetValue("format", out var format) && format as string == TakeFormat.Name))
                throw new FormatException("missing the take format marker");
            return new TakeInfo
            {
                Id = id,
                Name = take["name"] as string ?? id,
                Created = take["created"] as string ?? "",
                Duration = Convert.ToDouble(take["durationSeconds"]),
                Frames = Convert.ToInt32(take["frameCount"]),
                People = Convert.ToInt32(take["people"]),
                Mode = take["mode"] as string ?? "standing",
            };
        }

        /// <summary>A file name that is not taken yet: "name", then "name (2)", "name (3)" and so on.</summary>
        string UniqueId(string stem, string allowed)
        {
            var id = stem;
            bool Taken(string candidate) =>
                (takes.ContainsKey(candidate) || File.Exists(JsonPath(candidate))) &&
                !string.Equals(candidate, allowed, StringComparison.OrdinalIgnoreCase);
            for (int n = 2; Taken(id); n++)
                id = $"{stem} ({n})";
            return id;
        }

        /// <summary>Keeps letters, digits, spaces and a few safe symbols; everything else becomes a dash.</summary>
        static string SafeFileName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')' ? c : '-');
            var result = sb.ToString().Trim(' ', '.');
            return result.Length == 0 ? "take" : result;
        }

        /// <summary>Writes to a temporary file first, so a crash mid-save never leaves a half-written take.</summary>
        static void WriteAtomically(string path, string text)
        {
            var temp = path + ".saving";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }

    static class TakeFormat
    {
        public const string Name = "kinect-web-console-take";
        public const int Version = 1;
    }
}
