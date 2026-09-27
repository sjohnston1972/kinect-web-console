using KinectBridge.Sensor;

namespace KinectBridge.Fusion
{
    /// <summary>
    /// Turntable mode's filter: keeps only depth readings inside the scanning box and above the floor.
    ///
    /// Kinect Fusion works out movement from everything it sees. When the Kinect stays still and the object
    /// turns (a person on a swivel chair, a thing on a lazy Susan), the floor and room stay still and argue
    /// with the object. Hiding everything but the object makes turning it look the same as walking round it.
    ///
    /// Positions use the skeleton tracker's convention: metres, X to the right of the picture, Y up, Z away
    /// from the Kinect; the floor plane comes from the skeleton tracker too.
    /// </summary>
    class TurntableFilter
    {
        const double Focal = 571.0;          // the depth camera's focal length in pixels at 640x480 (SDK nominal value)
        const double AboveFloor = 0.03;      // readings within 3 cm of the floor, or below it, are dropped

        readonly double minX, maxX, minY, maxY, minZ, maxZ;
        readonly float[] floor;              // A, B, C, D with Ax + By + Cz + D = 0, or null if not seen

        /// <param name="floorHeight">The Kinect's height above the floor when the preset stands the box on it, otherwise null.</param>
        public TurntableFilter(ScanPreset preset, float[] floor, double? floorHeight)
        {
            this.floor = floor;
            minZ = preset.StartDistance;
            maxZ = preset.StartDistance + preset.SizeZ;
            maxX = preset.SizeX / 2;
            minX = -maxX;
            if (floorHeight != null)
            {
                minY = -floorHeight.Value - 0.05;   // the box stands 5 cm below the floor, like the scanner's placement
                maxY = minY + preset.SizeY;
            }
            else
            {
                maxY = preset.SizeY / 2;
                minY = -maxY;
            }
        }

        /// <summary>True to keep the reading at pixel (u, v), mm away; false to hide it from Kinect Fusion.</summary>
        public bool Keep(int u, int v, int mm)
        {
            if (mm <= 0) return false;
            double z = mm / 1000.0;
            if (z < minZ || z > maxZ) return false;
            double x = (u - DepthFrame.Width / 2) * z / Focal;
            if (x < minX || x > maxX) return false;
            double y = (DepthFrame.Height / 2 - v) * z / Focal;
            if (y < minY || y > maxY) return false;
            if (floor != null && floor[0] * x + floor[1] * y + floor[2] * z + floor[3] < AboveFloor) return false;
            return true;
        }
    }
}
