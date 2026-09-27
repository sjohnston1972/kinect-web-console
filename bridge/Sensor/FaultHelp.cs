namespace KinectBridge.Sensor
{
    /// <summary>
    /// Turns a sensor state into what the page shows: the status light colour,
    /// a short title, and plain-English advice on what to check.
    /// </summary>
    class FaultHelp
    {
        public string Light;   // green, amber or red
        public string Title;
        public string Help;

        const string VmwareHint =
            " If VMware is running, it may have taken part of the Kinect: in VMware choose VM, Removable Devices, " +
            "and pick Disconnect (Connect to host) for any Microsoft, Xbox NUI or Kinect entry.";

        public static FaultHelp For(SensorState state, bool isMock)
        {
            if (isMock)
            {
                // Mock mode is always amber, so it is never mistaken for the real Kinect
                return state == SensorState.Ready
                    ? Make("amber", "Mock mode: using a fake sensor",
                        "No Kinect is being used. To use the real Kinect, close the black Kinect Web Console window and double-click run.cmd.")
                    : Make("amber", "Mock mode: starting the fake sensor", "This takes a moment.");
            }

            switch (state)
            {
                case SensorState.Ready:
                    return Make("green", "Kinect ready", "");
                case SensorState.Initialising:
                    return Make("amber", "Starting the Kinect", "This takes a few seconds.");
                case SensorState.NoSensor:
                    return Make("red", "No Kinect found",
                        "Check the Kinect's USB lead is plugged straight into the PC, not a hub, and its adapter is plugged into the wall." + VmwareHint);
                case SensorState.NotPowered:
                    return Make("red", "The Kinect has no power",
                        "Check the adapter's mains plug and the wall switch. The green light on the Kinect should be on." + VmwareHint);
                case SensorState.InUse:
                    return Make("red", "Another program is using the Kinect",
                        "Close other Kinect programs, such as the Developer Toolkit samples. The app picks the Kinect up again within a few seconds, or press Reconnect.");
                case SensorState.BadUsb:
                    return Make("red", "The USB port cannot keep up",
                        "Plug the Kinect into a different USB port directly on the PC. A USB 2 port often works best.");
                default:
                    return Make("red", "The Kinect reported an error",
                        "Unplug the Kinect's USB lead, wait 10 seconds and plug it back in." + VmwareHint);
            }
        }

        static FaultHelp Make(string light, string title, string help)
        {
            return new FaultHelp { Light = light, Title = title, Help = help };
        }
    }
}
