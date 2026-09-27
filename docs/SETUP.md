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

## 3. Kinect software

Both are installed on this PC. They come from the Microsoft Download Center, are signed by Microsoft, and were installed silently. Windows asks for administrator approval once for each.

1. **Kinect for Windows SDK v1.8** (file `KinectSDK-v1.8-Setup.exe`, 222 MB). Installs the drivers, runtime and the Microsoft.Kinect library.
2. **Kinect for Windows Developer Toolkit v1.8** (file `KinectDeveloperToolkit-v1.8.0-Setup.exe`, 384 MB). Installs the sample apps and the Kinect Fusion libraries.

To install again by hand, run each file with `/quiet /norestart`, SDK first.

The installers also set two system settings that point at the install folders: `KINECTSDK10_DIR` and `KINECT_TOOLKIT_DIR`.

## 4. Kinect library files

The bridge program links to these files. They were found on disk, not assumed.

| File | Purpose | Version | Path on this PC |
| --- | --- | --- | --- |
| Microsoft.Kinect.dll | Talks to the sensor: colour, depth, skeleton, tilt | 1.8.0.595 | `C:\Program Files\Microsoft SDKs\Kinect\v1.8\Assemblies\Microsoft.Kinect.dll` |
| Microsoft.Kinect.Toolkit.Fusion.dll | The C# side of Kinect Fusion | 1.8.0.572 | `C:\Program Files\Microsoft SDKs\Kinect\Developer Toolkit v1.8.0\Assemblies\Microsoft.Kinect.Toolkit.Fusion.dll` |
| KinectFusion180_64.dll | The native 64-bit Fusion engine, copied next to the program | 1.8.0.572 | `C:\Program Files\Microsoft SDKs\Kinect\Developer Toolkit v1.8.0\Redist\amd64\KinectFusion180_64.dll` |

## 5. Hardware check

1. Plug in the Kinect. The green light on the front comes on.
2. Open Device Manager. A Kinect for Windows group appears with no yellow warning icons.
3. Open the Developer Toolkit Browser and run Skeleton Basics. A stick figure follows you.
4. Run Kinect Fusion Explorer. A 3D surface builds as you move the Kinect.
5. Close both samples. Only one program can use the Kinect at a time.

**Troubleshooting:** if Device Manager shows only "Xbox NUI Motor" and no camera, the Kinect is getting USB but not mains power. Check the adapter's wall plug. The Kinect only shows its motor to the PC until mains power reaches it.

**Troubleshooting: VMware.** VMware Workstation is installed on this PC. While a virtual machine is running, VMware can take part of the Kinect (usually the audio part) into the virtual machine. The Kinect library then reports the sensor as "NotPowered" and the green light stays off, even though power is fine. Device Manager shows a "VMware USB Device" in place of a Kinect entry. Fix: in VMware, open VM, then Removable Devices, and choose "Disconnect (Connect to host)" for any Microsoft, Xbox NUI or Kinect entry. To stop it happening again, set VMware's Edit, Preferences, USB option to connect new devices to the host.

**Last check (27 Sep 2026):** sensor status Connected, colour 30 fps, depth about 27 fps in the first 3 seconds, tilt motor reading 13 degrees. Skeleton tracking followed one person at 30 frames per second for 20 seconds, head about 1.3 m from the sensor. Kinect Fusion Explorer built a 3D surface (checked by Steven).

## 6. Web address reservation

The bridge serves the page at `http://localhost:8766/`. No reservation is needed: Windows lets a normal user listen on a `localhost` address, and Phase 1 confirmed the bridge starts without administrator rights.

Port 8766 is used because ShellMate-Portable already uses 8765 on this PC. To change the port, edit `"port"` in `settings.json`.
