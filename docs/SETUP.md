# Setup

One-time steps to get this PC ready to build and run Kinect Web Console. Each step lists what you should see when it has worked.

## 1. Build tools

| Tool | Version found | How it got here |
| --- | --- | --- |
| Git | 2.51.0 | Already installed |
| .NET SDK | 10.0.401 | Installed with `winget install --id Microsoft.DotNet.SDK.10 -e` |
| .NET Framework 4.8.1 | Built into Windows 11 (release 533509) | The runtime the app runs on |
| .NET Framework 4.8.1 Developer Pack | 4.8.1 | Installed with `winget install --id Microsoft.DotNet.Framework.DeveloperPack_4 -e`. Supplies the files needed to build for .NET Framework |

Check: open PowerShell and run `dotnet --version` and `git --version`. Each prints a version number.

## 2. Graphics card

| Card | DirectX feature levels | Result |
| --- | --- | --- |
| AMD Radeon RX 7700 XT | up to 12_2, includes 11_0 | Suitable for Kinect Fusion on the GPU |
| AMD Radeon Graphics (built into the processor) | up to 12_2, includes 11_0 | Suitable, but slower |

Kinect Fusion needs DirectX 11. The RX 7700 XT is the one to use. Checked with `dxdiag /t`.

## 3. Kinect software (Steven installs)

Both come from the Microsoft Download Center and use a normal graphical installer.

1. **Kinect for Windows SDK v1.8** (file `KinectSDK-v1.8-Setup.exe`). Installs the drivers, runtime and the Microsoft.Kinect library.
2. **Kinect for Windows Developer Toolkit v1.8** (file `KinectDeveloperToolkit-v1.8.0-Setup.exe`). Installs the sample apps and the Kinect Fusion libraries.

Install the SDK first, with the Kinect unplugged, then the Toolkit. Plug the Kinect in afterwards.

## 4. Kinect library files

The bridge program links to these files. They are found on disk, not assumed.

| File | Purpose | Path on this PC |
| --- | --- | --- |
| Microsoft.Kinect.dll | Talks to the sensor: colour, depth, skeleton, tilt | Not found yet: SDK not installed |
| Microsoft.Kinect.Toolkit.Fusion.dll | The C# side of Kinect Fusion | Not found yet: Toolkit not installed |
| KinectFusion180_64.dll | The native 64-bit Fusion engine, copied next to the program | Not found yet: Toolkit not installed |

## 5. Hardware check

1. Plug in the Kinect. The green light on the front comes on.
2. Open Device Manager. A Kinect for Windows group appears with no yellow warning icons.
3. Open the Developer Toolkit Browser and run Skeleton Basics. A stick figure follows you.
4. Run Kinect Fusion Explorer. A 3D surface builds as you move the Kinect.
5. Close both samples. Only one program can use the Kinect at a time.

**What Windows sees right now:** a single device called "Xbox NUI Motor" with an error, and no camera or audio devices. That is expected while the SDK is missing, because there is no driver yet. It can also mean the power adapter is not plugged into the mains: without mains power the Kinect only shows its motor to the PC, and the camera never appears. After installing the SDK, Device Manager should show a "Kinect for Windows" group with Camera, Audio Array and Device entries.

## 6. Web address reservation

The bridge serves the page at `http://localhost:8765/`. Windows lets a normal user listen on a `localhost` address without a reservation, so no administrator command is expected. If Phase 1 finds otherwise, the one-time command goes here.
