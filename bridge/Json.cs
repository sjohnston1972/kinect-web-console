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

        /// <summary>Parses a JSON object. Throws if the text is not a JSON object.</summary>
        public static Dictionary<string, object> Parse(string text)
        {
            var result = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (result == null) throw new System.FormatException("Expected a JSON object");
            return result;
        }
    }
}
