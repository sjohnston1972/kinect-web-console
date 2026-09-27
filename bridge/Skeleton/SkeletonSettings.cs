namespace KinectBridge.Skeleton
{
    enum TrackingMode { Standing, Seated }

    enum Smoothing { Off, Light, Heavy }

    /// <summary>
    /// How skeleton tracking runs: standing (all 20 joints) or seated (10 upper-body joints),
    /// and how much the SDK smooths out jitter. More smoothing means steadier joints that lag slightly behind.
    /// </summary>
    class SkeletonSettings
    {
        public TrackingMode Mode = TrackingMode.Standing;
        public Smoothing Smoothing = Smoothing.Light;

        public SkeletonSettings Copy() => new SkeletonSettings { Mode = Mode, Smoothing = Smoothing };

        public string ModeName => Mode == TrackingMode.Seated ? "seated" : "standing";

        public string SmoothingName => Smoothing.ToString().ToLowerInvariant();

        public static bool TryParseMode(string text, out TrackingMode mode)
        {
            mode = TrackingMode.Standing;
            if (text == "standing") return true;
            if (text == "seated") { mode = TrackingMode.Seated; return true; }
            return false;
        }

        public static bool TryParseSmoothing(string text, out Smoothing smoothing)
        {
            switch (text)
            {
                case "off": smoothing = Smoothing.Off; return true;
                case "light": smoothing = Smoothing.Light; return true;
                case "heavy": smoothing = Smoothing.Heavy; return true;
                default: smoothing = Smoothing.Light; return false;
            }
        }
    }
}
