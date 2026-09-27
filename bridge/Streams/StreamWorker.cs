using System;
using System.Threading;

namespace KinectBridge.Streams
{
    /// <summary>
    /// A background thread that runs one job whenever it is signalled.
    /// Several signals while it is busy count as one, so the job always runs on the newest frame
    /// and never works through a backlog. Errors are logged (at most every 10 seconds) and the thread carries on.
    /// </summary>
    class StreamWorker : IDisposable
    {
        static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(10);

        readonly string name;
        readonly Action job;
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool stopping;
        DateTime lastErrorLogged = DateTime.MinValue;

        public StreamWorker(string name, Action job)
        {
            this.name = name;
            this.job = job;
            thread = new Thread(Loop) { IsBackground = true, Name = name };
            thread.Start();
        }

        public void Signal() { signal.Set(); }

        void Loop()
        {
            while (true)
            {
                signal.WaitOne();
                if (stopping) return;
                try
                {
                    job();
                }
                catch (Exception ex)
                {
                    if (DateTime.UtcNow - lastErrorLogged > LogEvery)
                    {
                        lastErrorLogged = DateTime.UtcNow;
                        Log.Error($"{name} failed: {ex.Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            signal.Set();
        }
    }
}
