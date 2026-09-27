using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using KinectBridge.Fusion;
using KinectBridge.Mocap;
using KinectBridge.Sensor;
using KinectBridge.Skeleton;
using KinectBridge.Streams;
using KinectBridge.Web;

namespace KinectBridge
{
    /// <summary>
    /// Start point. Reads settings, picks the real or mock sensor, starts the web server,
    /// opens the page, then waits until the window is closed or Ctrl+C is pressed.
    ///
    /// Options:
    ///   --mock        use the fake sensor instead of the Kinect
    ///   --no-browser  do not open the page automatically
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            var mock = args.Contains("--mock");
            var openBrowser = !args.Contains("--no-browser");

            string root;
            try
            {
                root = AppSettings.FindRootFolder();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return Fail();
            }

            Log.Init(Path.Combine(root, "logs"));
            var settings = AppSettings.Load(root);
            Console.Title = "Kinect Web Console" + (mock ? " (mock mode)" : "");
            Log.Info($"Kinect Web Console starting, {(mock ? "mock mode" : "real Kinect")}, folder {root}");

            CreateCaptureFolders(settings);

            ISensor sensor = mock ? (ISensor)new MockSensor() : new KinectSensorSource();
            var hub = new MessageHub();
            var tilt = new TiltController(sensor);
            var pump = new StreamPump(sensor, hub, settings);
            var skeletonSettings = new SkeletonSettings();
            sensor.ApplySkeletonSettings(skeletonSettings);
            var skeletons = new SkeletonPump(sensor, hub);
            var status = new StatusReporter(sensor, hub, pump, tilt, skeletons, skeletonSettings);
            var takes = new TakeLibrary(Path.Combine(settings.CapturesPath, "mocap"));
            var recorder = new Recorder(sensor, hub, takes, skeletonSettings);
            hub.Greetings.Add(() => recorder.Message());
            var scanner = new Scanner(sensor, pump, skeletons, hub, settings);
            hub.Greetings.Add(() => scanner.Message());
            Commands.Register(hub, sensor, tilt, pump, skeletonSettings, status.Push, recorder, takes, scanner);
            var server = new WebServer(settings, hub);

            try
            {
                server.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 32 || ex.ErrorCode == 183)
            {
                // Port already taken. If it is this app, just show its page; otherwise explain the clash.
                if (IsThisAppAt(server.Address))
                {
                    Log.Info("Kinect Web Console is already running, so opening its page instead.");
                    if (openBrowser) OpenPage(server.Address);
                    Thread.Sleep(3000);
                    return 0;
                }
                Log.Error($"Another program is already using port {settings.Port}. Close it, or change \"port\" in settings.json to a free number.");
                return Fail();
            }
            catch (HttpListenerException ex)
            {
                Log.Error($"Could not start the web server on {server.Address}: {ex.Message} (code {ex.ErrorCode}). See docs\\SETUP.md.");
                return Fail();
            }

            sensor.Start();
            status.Start();

            Console.WriteLine();
            Console.WriteLine($"  Kinect Web Console is running at {server.Address}");
            Console.WriteLine("  Keep this window open while you use it. Close it to stop the app.");
            Console.WriteLine();

            if (openBrowser) OpenPage(server.Address);

            var stop = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            stop.WaitOne();

            Log.Info("Stopping");
            status.Dispose();
            pump.Dispose();
            skeletons.Dispose();
            recorder.Dispose();
            scanner.Dispose();
            sensor.Dispose();
            server.Dispose();
            return 0;
        }

        static void CreateCaptureFolders(AppSettings settings)
        {
            try
            {
                foreach (var sub in new[] { "snapshots", "scans", "mocap" })
                    Directory.CreateDirectory(Path.Combine(settings.CapturesPath, sub));
            }
            catch (Exception ex)
            {
                // Saving will fail later with its own message; the live view still works
                Log.Error($"Could not create the captures folder {settings.CapturesPath}: {ex.Message}");
            }
        }

        static void OpenPage(string address)
        {
            try
            {
                Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open the browser ({ex.Message}). Open {address} yourself.");
            }
        }

        /// <summary>Asks whatever is on the port for its page and checks the title is ours.</summary>
        static bool IsThisAppAt(string address)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(address);
                request.Timeout = 2000;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return reader.ReadToEnd().Contains("<title>Kinect Web Console</title>");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Keeps the window open after a startup error so the message can be read.</summary>
        static int Fail()
        {
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to close this window.");
                Console.ReadKey(true);
            }
            return 1;
        }
    }
}
