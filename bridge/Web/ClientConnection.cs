using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KinectBridge.Web
{
    /// <summary>
    /// One open browser tab, connected over the WebSocket.
    ///
    /// Outgoing messages go into one of two places:
    /// - a "latest" slot per stream (status, video frames, skeletons). A new message replaces
    ///   an unsent older one, so a slow browser always gets the newest data and nothing queues up.
    ///   This is the spec's slow browser rule: a queue depth of one per stream.
    /// - an ordered queue for messages that must all arrive (log lines, errors, replies).
    ///   It is capped, and the oldest are dropped if a browser stops reading altogether.
    /// A single send loop drains both, because a WebSocket allows only one send at a time.
    /// </summary>
    class ClientConnection
    {
        const int MaxOrdered = 500;
        const int MaxIncomingBytes = 64 * 1024;

        struct Outgoing
        {
            public byte[] Data;
            public WebSocketMessageType Kind;
        }

        static int nextId;

        readonly WebSocket socket;
        readonly object gate = new object();
        readonly Queue<Outgoing> ordered = new Queue<Outgoing>();
        readonly Dictionary<string, Outgoing> latest = new Dictionary<string, Outgoing>();
        readonly List<string> latestOrder = new List<string>();   // slots in the order they first filled
        readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
        readonly CancellationTokenSource closing = new CancellationTokenSource();
        HashSet<string> subscriptions = new HashSet<string>();

        public readonly int Id = Interlocked.Increment(ref nextId);

        public ClientConnection(WebSocket socket)
        {
            this.socket = socket;
        }

        /// <summary>The streams this browser tab has asked for.</summary>
        public HashSet<string> Subscriptions
        {
            get { lock (gate) return new HashSet<string>(subscriptions); }
            set { lock (gate) subscriptions = value; }
        }

        /// <summary>Sends JSON text. With a slot name, only the newest message per slot is kept.</summary>
        public void SendText(string json, string slot = null)
        {
            Enqueue(new Outgoing { Data = Encoding.UTF8.GetBytes(json), Kind = WebSocketMessageType.Text }, slot);
        }

        /// <summary>Sends a binary frame. Always newest-only, per slot.</summary>
        public void SendBinary(byte[] data, string slot)
        {
            Enqueue(new Outgoing { Data = data, Kind = WebSocketMessageType.Binary }, slot);
        }

        void Enqueue(Outgoing message, string slot)
        {
            lock (gate)
            {
                if (slot == null)
                {
                    ordered.Enqueue(message);
                    while (ordered.Count > MaxOrdered) ordered.Dequeue();
                }
                else
                {
                    if (!latest.ContainsKey(slot)) latestOrder.Add(slot);
                    latest[slot] = message;
                }
            }
            // Wake the send loop. It may already be awake, which is fine.
            try { wake.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>Runs until the browser disconnects. Calls onText for each message received.</summary>
        public async Task RunAsync(Action<ClientConnection, string> onText)
        {
            var sending = SendLoop();
            try
            {
                await ReceiveLoop(onText);
            }
            finally
            {
                closing.Cancel();
                try { await sending; } catch { }
            }
        }

        async Task ReceiveLoop(Action<ClientConnection, string> onText)
        {
            var buffer = new byte[8192];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (Exception)
                {
                    return;   // browser went away without saying goodbye: normal when a tab closes
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxIncomingBytes)
                {
                    Log.Warn($"Browser {Id} sent a message over {MaxIncomingBytes / 1024} KB, closing the connection");
                    try { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", CancellationToken.None); } catch { }
                    return;
                }
                if (!result.EndOfMessage) continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    onText(this, Encoding.UTF8.GetString(message.ToArray()));
                message.SetLength(0);
            }
        }

        async Task SendLoop()
        {
            try
            {
                while (!closing.IsCancellationRequested)
                {
                    await wake.WaitAsync(closing.Token);
                    Outgoing next;
                    while (TryTakeNext(out next))
                    {
                        await socket.SendAsync(new ArraySegment<byte>(next.Data), next.Kind, true, closing.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (socket.State == WebSocketState.Open) Log.Warn($"Sending to browser {Id} failed: {ex.Message}");
            }
        }

        /// <summary>Ordered messages first, then the newest message in each slot.</summary>
        bool TryTakeNext(out Outgoing next)
        {
            lock (gate)
            {
                if (ordered.Count > 0)
                {
                    next = ordered.Dequeue();
                    return true;
                }
                if (latestOrder.Count > 0)
                {
                    var slot = latestOrder[0];
                    latestOrder.RemoveAt(0);
                    next = latest[slot];
                    latest.Remove(slot);
                    return true;
                }
                next = default(Outgoing);
                return false;
            }
        }
    }
}
