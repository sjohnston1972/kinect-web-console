using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace KinectBridge.Web
{
    /// <summary>
    /// The HTTP server: serves the web folder, lets people download from the captures folder,
    /// and upgrades /ws to the WebSocket that carries all live data.
    ///
    /// Local only, in three layers:
    /// 1. It registers the address http://localhost:PORT/ with Windows, so requests
    ///    addressed to this PC's network IP are turned away by Windows itself.
    /// 2. Any request that still arrives from an address other than this PC gets 403 Forbidden.
    /// 3. The WebSocket only accepts pages served by this app, so another website open in the
    ///    browser cannot connect to the Kinect behind your back.
    /// </summary>
    class WebServer : IDisposable
    {
        static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" },
            { ".js", "text/javascript; charset=utf-8" },
            { ".json", "application/json; charset=utf-8" },
            { ".png", "image/png" },
            { ".jpg", "image/jpeg" },
            { ".svg", "image/svg+xml" },
            { ".ico", "image/x-icon" },
            { ".bvh", "text/plain; charset=utf-8" },
            { ".obj", "text/plain; charset=utf-8" },
            { ".stl", "model/stl" },
            { ".ply", "application/octet-stream" },
        };

        readonly AppSettings settings;
        readonly MessageHub hub;
        readonly HttpListener listener = new HttpListener();
        readonly string webRoot;
        readonly string capturesRoot;

        public string Address => $"http://localhost:{settings.Port}/";

        public WebServer(AppSettings settings, MessageHub hub)
        {
            this.settings = settings;
            this.hub = hub;
            webRoot = Path.GetFullPath(settings.WebPath);
            capturesRoot = settings.CapturesPath;
        }

        /// <summary>Starts listening. Throws HttpListenerException if the port is taken.</summary>
        public void Start()
        {
            listener.Prefixes.Add(Address);
            listener.Start();
            Task.Run(AcceptLoop);
        }

        async Task AcceptLoop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;   // the listener was stopped
                }
                // Each request on its own task, so a long WebSocket session never blocks page loads
                var _ = Task.Run(() => Handle(context));
            }
        }

        async Task Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address))
                {
                    Log.Warn($"Refused a request from {request.RemoteEndPoint.Address}: this app only answers this PC");
                    Reply(response, 403, "This app only answers requests from this PC.");
                    return;
                }

                var path = request.Url.AbsolutePath;

                if (path == "/ws")
                {
                    await HandleWebSocket(context);
                    return;
                }

                if (request.HttpMethod != "GET" && request.HttpMethod != "HEAD")
                {
                    Reply(response, 405, "Only GET is supported.");
                    return;
                }

                var headOnly = request.HttpMethod == "HEAD";
                if (path.StartsWith("/captures/", StringComparison.OrdinalIgnoreCase))
                    ServeFile(response, capturesRoot, path.Substring("/captures/".Length), download: true, headOnly: headOnly);
                else
                    ServeFile(response, webRoot, path == "/" ? "index.html" : path.TrimStart('/'), download: false, headOnly: headOnly);
            }
            catch (Exception ex)
            {
                Log.Error($"Request for {request.Url.AbsolutePath} failed: {ex.Message}");
                try { Reply(response, 500, "Something went wrong. The bridge log has details."); } catch { }
            }
        }

        async Task HandleWebSocket(HttpListenerContext context)
        {
            if (!context.Request.IsWebSocketRequest)
            {
                Reply(context.Response, 400, "This address is for the WebSocket connection.");
                return;
            }

            // Browsers always say which page opened the connection. Only our own page may connect.
            var origin = context.Request.Headers["Origin"];
            if (origin != null && !string.Equals(origin.TrimEnd('/'), Address.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Refused a WebSocket connection from a page at {origin}");
                Reply(context.Response, 403, "Only the Kinect Web Console page may connect.");
                return;
            }

            var socketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            await hub.RunClient(new ClientConnection(socketContext.WebSocket));
        }

        /// <summary>
        /// Sends one file from inside root. Anything that resolves outside root is refused,
        /// so a request like /captures/../../secret.txt cannot reach the rest of the disk.
        /// </summary>
        void ServeFile(HttpListenerResponse response, string root, string relative, bool download, bool headOnly)
        {
            string fullPath;
            try
            {
                var decoded = Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(root, decoded));
            }
            catch (Exception)
            {
                Reply(response, 404, "Not found.");   // names Windows cannot accept, such as ones with a colon
                return;
            }

            var inside = fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!inside || !File.Exists(fullPath))
            {
                Reply(response, 404, "Not found.");
                return;
            }

            response.ContentType = ContentTypes.TryGetValue(Path.GetExtension(fullPath), out var type) ? type : "application/octet-stream";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Cache-Control"] = "no-cache";   // always load the newest page after a rebuild
            if (download)
                response.Headers["Content-Disposition"] = $"attachment; filename=\"{Path.GetFileName(fullPath)}\"";

            var bytes = File.ReadAllBytes(fullPath);
            response.ContentLength64 = bytes.Length;
            if (!headOnly) response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        static void Reply(HttpListenerResponse response, int status, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        public void Dispose()
        {
            try { listener.Stop(); listener.Close(); } catch { }
        }
    }
}
