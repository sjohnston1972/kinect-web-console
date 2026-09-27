using KinectBridge.Skeleton;

namespace KinectBridge.Web
{
    /// <summary>Commands for the Skeleton tab: tracking mode and smoothing.</summary>
    static partial class Commands
    {
        static void RegisterSkeleton(AppParts p)
        {
            p.Hub.On("skeleton.settings", (client, msg) =>
            {
                SkeletonSettings updated;
                lock (p.SkeletonSettings)
                {
                    if (msg.TryGetValue("mode", out var m))
                    {
                        if (!SkeletonSettings.TryParseMode(m as string, out var mode))
                        {
                            MessageHub.SendError(client, "badSetting", "Tracking mode must be standing or seated.");
                            return;
                        }
                        p.SkeletonSettings.Mode = mode;
                    }
                    if (msg.TryGetValue("smoothing", out var s))
                    {
                        if (!SkeletonSettings.TryParseSmoothing(s as string, out var smoothing))
                        {
                            MessageHub.SendError(client, "badSetting", "Smoothing must be off, light or heavy.");
                            return;
                        }
                        p.SkeletonSettings.Smoothing = smoothing;
                    }
                    updated = p.SkeletonSettings.Copy();
                }
                p.Sensor.ApplySkeletonSettings(updated);
                p.Prefs.SkeletonMode = updated.ModeName;
                p.Prefs.SkeletonSmoothing = updated.SmoothingName;
                p.Prefs.Save();
                Log.Info($"Skeleton tracking: {updated.ModeName}, smoothing {updated.SmoothingName}");
                p.PushStatus();
            });
        }
    }
}
