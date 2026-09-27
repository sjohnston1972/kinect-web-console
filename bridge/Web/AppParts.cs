using System;
using KinectBridge.Fusion;
using KinectBridge.Mocap;
using KinectBridge.Sensor;
using KinectBridge.Skeleton;
using KinectBridge.Streams;

namespace KinectBridge.Web
{
    /// <summary>
    /// The parts of the bridge that commands act on, gathered in one place so each tab's commands
    /// (Commands.*.cs) can reach what they need. Built once at start-up by Program.
    /// </summary>
    class AppParts
    {
        public MessageHub Hub;
        public ISensor Sensor;
        public TiltController Tilt;
        public StreamPump Pump;
        public SkeletonPump Skeletons;
        public SkeletonSettings SkeletonSettings;
        public Action PushStatus;         // sends a fresh status message straight away
        public Recorder Recorder;
        public TakeLibrary Takes;
        public Scanner Scanner;
        public Preferences Prefs;
    }
}
