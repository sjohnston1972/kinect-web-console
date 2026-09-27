using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KinectBridge.Sensor;

namespace KinectBridge.Web
{
    /// <summary>
    /// The switchboard between the bridge and every open browser tab.
    /// Keeps the list of connections, sends messages out, and acts on messages coming in.
    /// Message names and contents are listed in docs/PROTOCOL.md.
    /// </summary>
    class MessageHub
    {
        public const int ProtocolVersion = 1;

        // The streams a browser may subscribe to. Later phases start sending them.
        static readonly HashSet<string> KnownStreams = new HashSet<string>
            { "colour", "depth", "depthRaw", "skeletons", "fusion" };

        readonly object gate = new object();
        readonly List<ClientConnection> clients = new List<ClientConnection>();
        readonly ISensor sensor;

        /// <summary>Builds the current status message. Set by StatusReporter.</summary>
        public Func<string> CurrentStatus;

        public MessageHub(ISensor sensor)
        {
            this.sensor = sensor;
            Log.LineAdded += line => Broadcast(Json.Serialize(LogMessage(line)));
        }

        /// <summary>Looks after one browser from connection until it closes.</summary>
        public async Task RunClient(ClientConnection client)
        {
            // Catch the new tab up: the current status and the recent log
            if (CurrentStatus != null) client.SendText(CurrentStatus(), "status");
            foreach (var line in Log.RecentLines()) client.SendText(Json.Serialize(LogMessage(line)));

            lock (gate) clients.Add(client);
            Log.Info($"Browser {client.Id} connected");
            try
            {
                await client.RunAsync(HandleText);
            }
            finally
            {
                lock (gate) clients.Remove(client);
                Log.Info($"Browser {client.Id} disconnected");
            }
        }

        /// <summary>Sends to every browser. With a slot, older unsent messages in that slot are replaced.</summary>
        public void Broadcast(string json, string slot = null)
        {
            ClientConnection[] targets;
            lock (gate) targets = clients.ToArray();
            foreach (var client in targets) client.SendText(json, slot);
        }

        void HandleText(ClientConnection client, string text)
        {
            // Fault isolation: a bad message is reported to that browser and logged, and nothing else is affected
            try
            {
                var message = Json.Parse(text);
                var type = message.TryGetValue("type", out var t) ? t as string : null;
                var version = message.TryGetValue("v", out var v) && v is int n ? n : 0;

                if (type == null) { SendError(client, "badMessage", "The message has no type."); return; }
                if (version != ProtocolVersion)
                {
                    SendError(client, "badVersion", $"The page speaks protocol version {version} but the bridge speaks {ProtocolVersion}. Reload the page.");
                    return;
                }

                switch (type)
                {
                    case "subscribe":
                        HandleSubscribe(client, message);
                        break;
                    case "sensor.reconnect":
                        sensor.Reconnect();
                        break;
                    default:
                        SendError(client, "unknownType", $"The bridge does not understand '{type}' messages yet.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not handle a message from browser {client.Id}: {ex.Message}");
                SendError(client, "badMessage", "The bridge could not read that message.");
            }
        }

        void HandleSubscribe(ClientConnection client, Dictionary<string, object> message)
        {
            var streams = new HashSet<string>();
            if (message.TryGetValue("streams", out var list) && list is object[] items)
                foreach (var item in items.OfType<string>())
                    if (KnownStreams.Contains(item)) streams.Add(item);

            client.Subscriptions = streams;
            Log.Info($"Browser {client.Id} subscribed to: {(streams.Count == 0 ? "no streams" : string.Join(", ", streams))}");
        }

        static void SendError(ClientConnection client, string code, string text)
        {
            client.SendText(Json.Serialize(new { type = "error", v = ProtocolVersion, code, message = text }));
        }

        static object LogMessage(LogLine line)
        {
            return new { type = "log", v = ProtocolVersion, time = line.Time.ToString("HH:mm:ss"), level = line.Level, text = line.Text };
        }
    }
}
