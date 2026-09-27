using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace KinectBridge
{
    /// <summary>
    /// JSON in and out, using the serializer built into .NET Framework (no extra packages).
    /// A new serializer per call because the built-in one is not safe to share between threads.
    /// </summary>
    static class Json
    {
        public static string Serialize(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value);
        }

        /// <summary>
        /// JSON laid out for people to read: one entry per line, indented. Lists of plain values
        /// (such as [512, 512, 512]) stay on one line.
        /// </summary>
        public static string SerializeIndented(object value)
        {
            var sb = new System.Text.StringBuilder();
            WriteIndented(sb, value, 0);
            return sb.ToString();
        }

        static void WriteIndented(System.Text.StringBuilder sb, object value, int depth)
        {
            var pad = new string(' ', depth * 2);
            if (value is IDictionary<string, object> dict)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                var i = 0;
                foreach (var pair in dict)
                {
                    sb.Append(pad).Append("  ").Append(Serialize(pair.Key)).Append(": ");
                    WriteIndented(sb, pair.Value, depth + 1);
                    sb.Append(++i < dict.Count ? ",\n" : "\n");
                }
                sb.Append(pad).Append('}');
            }
            else if (value is System.Collections.IList list && !(value is string))
            {
                var plain = true;
                foreach (var item in list) if (item is IDictionary<string, object> || item is System.Collections.IList && !(item is string)) plain = false;
                if (plain)
                {
                    var parts = new List<string>();
                    foreach (var item in list) parts.Add(Serialize(item));
                    sb.Append('[').Append(string.Join(", ", parts)).Append(']');
                    return;
                }
                sb.Append("[\n");
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(pad).Append("  ");
                    WriteIndented(sb, list[i], depth + 1);
                    sb.Append(i + 1 < list.Count ? ",\n" : "\n");
                }
                sb.Append(pad).Append(']');
            }
            else
            {
                sb.Append(Serialize(value));
            }
        }

        /// <summary>Parses a JSON object. Throws if the text is not a JSON object.</summary>
        public static Dictionary<string, object> Parse(string text)
        {
            var result = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
            if (result == null) throw new System.FormatException("Expected a JSON object");
            return result;
        }
    }
}
