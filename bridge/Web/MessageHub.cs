using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KinectBridge.Web
{
    /// <summary>
    /// The switchboard between the bridge and every open browser tab.
    /// Keeps the list of connections, sends messages out, and passes incoming messages
    /// to the handler registered for their type (see Commands.cs).
    /// Message names and contents are listed in docs/PROTOCOL.md.
    /// </summary>
    class MessageHub
    {
        public const int ProtocolVersion = 1;

        // The streams a browser may subscribe to
        static readonly HashSet<string> KnownStreams = new HashSet<string>
            { "colour", "depth", "depthRaw", "skeletons", "fusion" };

        readonly object gate = new object();
        readonly List<ClientConnection> clients = new List<ClientConnection>();
        readonly Dictionary<string, Action<ClientConnection, Dictionary<string, object>>> handlers =
            new Dictionary<string, Action<ClientConnection, Dictionary<string, object>>>();

        /// <summary>Builds the current status message. Set by StatusReporter.</summary>
        public Func<string> CurrentStatus;

        public MessageHub()
        {
            Log.LineAdded += line => Broadcast(Json.Serialize(LogMessage(line)));
            On("subscribe", HandleSubscribe);
        }

        /// <summary>Registers what to do when a browser sends a message of this type.</summary>
        public void On(string type, Action<ClientConnection, Dictionary<string, object>> handler)
        {
            handlers[type] = handler;
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
            foreach (var client in Snapshot()) client.SendText(json, slot);
        }

        /// <summary>Sends a binary frame to the browsers subscribed to that stream, newest-only.</summary>
        public void BroadcastBinary(string stream, byte[] message)
        {
            foreach (var client in Snapshot())
                if (client.IsSubscribed(stream)) client.SendBinary(message, stream);
        }

        /// <summary>True if any open browser tab wants this stream. Checked before doing any encoding work.</summary>
        public bool AnySubscribed(string stream)
        {
            return Snapshot().Any(c => c.IsSubscribed(stream));
        }

        /// <summary>Sends a JSON message to one browser, in order.</summary>
        public static void Send(ClientConnection client, object message)
        {
            client.SendText(Json.Serialize(message));
        }

        public static void SendError(ClientConnection client, string code, string text)
        {
            Send(client, new { type = "error", v = ProtocolVersion, code, message = text });
        }

        ClientConnection[] Snapshot()
        {
            lock (gate) return clients.ToArray();
        }

        void HandleText(ClientConnection client, string text)
        {
            // Fault isolation: a bad message is reported to that browser and logged, and nothing else is affected
            string type = null;
            try
            {
                var message = Json.Parse(text);
                type = message.TryGetValue("type", out var t) ? t as string : null;
                var version = message.TryGetValue("v", out var v) && v is int n ? n : 0;

                if (type == null) { SendError(client, "badMessage", "The message has no type."); return; }
                if (version != ProtocolVersion)
                {
                    SendError(client, "badVersion", $"The page speaks protocol version {version} but the bridge speaks {ProtocolVersion}. Reload the page.");
                    return;
                }

                if (handlers.TryGetValue(type, out var handler)) handler(client, message);
                else SendError(client, "unknownType", $"The bridge does not understand '{type}' messages yet.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not handle {(type == null ? "a message" : "a '" + type + "' message")} from browser {client.Id}: {ex.Message}");
                SendError(client, "badMessage", "The bridge could not act on that message.");
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

        static object LogMessage(LogLine line)
        {
            return new { type = "log", v = ProtocolVersion, time = line.Time.ToString("HH:mm:ss"), level = line.Level, text = line.Text };
        }
    }
}
