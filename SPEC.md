# Kinect Web Console: Build Spec

Sep 27, 2026 · @Steven

## Purpose and scope

Build Kinect Web Console: a local app on Steven's Windows 11 PC that reads an Xbox 360 Kinect and presents it in a web browser at http://localhost. The browser page shows live video and depth, tracks skeletons, records motion capture, and builds 3D scans.

Claude Code builds from this spec. Steven reviews the work and is not a programmer, so every phase ends with a short plain-English summary of what was built and how to test it.

**In scope**

- Hardware and driver setup, verified before any code is written
- Live colour and depth views
- Skeleton tracking, overlaid on the video and shown in a 3D view
- Motion capture recording, playback, and export (JSON, and BVH for Blender)
- 3D scanning with Kinect Fusion, exported as STL, OBJ and PLY
- Sensor status and tilt motor control
- A mock sensor mode, so the interface can be built and tested with the Kinect unplugged

**Out of scope for this version**

- Access from any other device: the app listens on this PC only
- The microphone array and speech recognition
- More than one Kinect at a time
- The Xbox One Kinect (v2)

## Environment and hardware

The Kinect needs a power adapter, Microsoft's Kinect for Windows SDK v1.8, and a native Windows build toolchain. Phase 0 confirms every item below before any code is written.

| Item | Detail | Done by |
| --- | --- | --- |
| Kinect for Xbox 360 | Model 1414 or 1473, printed on the underside of the base | Steven |
| Kinect power and USB adapter | Splits the Kinect's proprietary plug into a USB lead and a mains power supply | Steven |
| USB port | Plugged directly into the PC, not a hub. If the Kinect drops out on a USB 3 port, try a USB 2 port | Steven |
| Kinect for Windows SDK v1.8 | Drivers, runtime, and the Microsoft.Kinect library. From the Microsoft Download Center | Claude Code downloads and installs silently. Windows asks Steven to approve the install |
| Kinect for Windows Developer Toolkit v1.8 | Sample apps and the Kinect Fusion libraries used for 3D scanning | Claude Code downloads and installs silently. Windows asks Steven to approve the install |
| Graphics card | DirectX 11 capable, needed for Kinect Fusion on the GPU | Claude Code checks |
| .NET Framework 4.8.1 | The runtime the app targets. Built into Windows 11 | Already present |
| .NET Framework 4.8.1 Developer Pack | The files needed to build for .NET Framework 4.8.1 | Claude Code installs with winget |
| .NET SDK 8 or later | Builds the project from the command line | Claude Code installs with winget |
| Git | Version control for the project | Claude Code installs with winget |

**Rule: Claude Code runs natively on Windows (PowerShell or Git Bash), never inside WSL.** WSL is a Linux virtual machine that cannot see the Kinect, and SDK 1.8 is Windows only.

**Hardware check, done by Steven before Phase 1**

1. Plug in the Kinect. The green light on the front comes on.
2. Open Device Manager. A Kinect for Windows group appears with no yellow warning icons.
3. Open the Developer Toolkit Browser and run Skeleton Basics. A stick figure follows you.
4. Run Kinect Fusion Explorer. A 3D surface builds as you move the Kinect.
5. Close both samples. Only one program can use the Kinect at a time.

*Plain English:* this is the physical and link layer. If the samples work, the cable, power, drivers and sensor are all good, and any later fault is in our code.

## Architecture

One C# program, KinectBridge, owns the Kinect and serves the web page. The browser never touches the Kinect directly.

&#91;embedded content: system architecture · Kinect to browser\]

The Kinect's data flows down through the bridge, which encodes it and streams it to the browser over one WebSocket. Saved scans and recordings land in the captures folder.

**Why C# and not Python.** The SDK 1.8 interfaces are C# (.NET Framework) and C++ only. Skeleton tracking and Kinect Fusion exist only in that SDK. The Python wrappers for it are abandoned and need Python 2.7, and the open-source libfreenect driver has no skeleton tracking and conflicts with Microsoft's driver.

**Technical decisions**

- **Project:** an SDK-style .csproj targeting net481 (.NET Framework 4.8.1), platform x64, built with `dotnet build`. Reference Microsoft.Kinect.dll and Microsoft.Kinect.Toolkit.Fusion.dll from the SDK and Toolkit install folders. Locate them on disk; do not assume paths. Copy the x64 native Kinect Fusion DLL into the output folder.
- **Web server:** the built-in HttpListener with WebSocket support, on http://localhost:8765/. If Windows needs a one-time URL reservation, document the command in docs/SETUP.md. The app must not need administrator rights on every run.
- **Front end:** plain HTML, CSS and JavaScript with no build step. Three.js is saved into web/vendor for the 3D views. No CDN and no internet access at run time.
- **Settings:** a settings.json file holds the port, captures folder, and stream quality.
- **Mock mode:** running with `--mock` swaps the real sensor for a fake one that produces test frames and a walking skeleton.

*Plain English:* the bridge works like a router terminating a proprietary serial link and republishing the traffic on Ethernet. The WebSocket is a session that stays open, like SSH, rather than a fresh request for every frame.

## Project layout

The project lives in one folder, `C:\local cc projects\kinect`, split so each folder has one job. This folder is the Git repository root and the folder Claude Code works in.

```
C:\local cc projects\kinect\
├── README.md              What it is and how to run it
├── SPEC.md                This spec
├── run.cmd                Double-click to start the bridge and open the page
├── settings.json          Port, captures folder, stream quality
├── bridge/                The C# program that talks to the Kinect
│   ├── KinectBridge.csproj   The build recipe: target and references
│   ├── Program.cs            Start point: reads settings, starts everything
│   ├── Sensor/               Real sensor and mock sensor, same interface
│   ├── Streams/              Turns frames into JPEG and raw depth messages
│   ├── Skeleton/             Joint data, smoothing, coordinate mapping
│   ├── Mocap/                Recording, playback, JSON and BVH export
│   ├── Fusion/               3D scanning and mesh export
│   └── Web/                  HTTP server, WebSocket, message handling
├── web/                   Everything the browser loads
│   ├── index.html
│   ├── css/
│   ├── js/                   One file per tab, plus the connection code
│   └── vendor/three/         Three.js, saved locally
├── captures/              Saved output, excluded from Git
│   ├── snapshots/
│   ├── scans/
│   └── mocap/
└── docs/
    ├── SETUP.md              One-time setup steps and expected results
    └── PROTOCOL.md           The message list below, kept current
```

*Plain English:* the folders work like VLANs. A fault in 3D scanning stays inside Fusion and does not spill into the live view. The real and mock sensors are like two SFP modules that fit the same port: the rest of the code does not care which one is plugged in.

## Features

Five features, each a tab in the web page, all fed by the same sensor connection. Sensor limits: colour and depth at 640x480 and 30 fps, depth range about 0.8 to 4 m, up to 2 people fully tracked with 20 joints each. Near mode is not available on the Xbox 360 Kinect.

### Live view

- Colour and depth side by side, or either one full width.
- Depth colourised by distance (near warm, far cool), with a legend in metres.
- Optional highlight of tracked people, using the player index built into each depth pixel.
- A 3D point cloud view of the depth data that the mouse can orbit.
- Snapshot button saves the current colour and depth images as PNG to captures/snapshots.
- Frames per second shown for each stream.

### Skeleton tracking

- Joints and bones drawn over the colour or depth image, mapped with the SDK's coordinate mapper so they line up.
- A 3D view of the skeleton in metres that the mouse can orbit.
- Standing mode (20 joints) and seated mode (10 upper-body joints), switchable.
- Joints the SDK infers rather than sees are drawn faded.
- Smoothing presets (off, light, heavy) using the SDK's built-in smoothing.

### Motion capture

- Record button with a 3-second countdown, so Steven can step into position.
- Records every skeleton frame with a timestamp: joint positions, tracking state, and bone rotations.
- Saves each take to captures/mocap as JSON, named by date and time.
- Take list with play, pause, a scrub bar, rename and delete.
- Exports a take as BVH for Blender: joint hierarchy from the Kinect skeleton, rotations from the SDK's hierarchical bone orientations, hip position as the root.

### 3D scanning

- Start, pause, reset and export controls for Kinect Fusion.
- Volume presets as starting points, tuned in Phase 5: Object (small, high detail), Person (about head height), Room (low detail).
- Live shaded preview of the model as it builds.
- Clear warning when Fusion loses tracking, with a reset option.
- Exports as STL, OBJ, and PLY (with colour when colour capture is on) to captures/scans.
- Falls back to CPU processing when no suitable GPU is found, with a warning that it will be slow.

### Status and controls

- Status light in the page header: green (sensor ready), amber (mock mode or initialising), red (no sensor, no power, or another program is using it).
- Plain-English help for each fault, for example: no power means check the adapter's mains plug.
- Tilt control from -27 to +27 degrees. Limited to one change per second and 15 changes in any 20 seconds, to protect the motor.
- Accelerometer reading, so Steven can see if the Kinect is level.
- The last 50 lines of the bridge log.

## Data link

One WebSocket at ws://localhost:8765/ws carries everything live: images as binary messages, everything else as JSON text. Every JSON message has a `type` field and a protocol version `v: 1`. Claude Code keeps docs/PROTOCOL.md in step with the code.

**Binary messages (bridge to browser).** Byte 0 is the stream type, bytes 1 to 8 are a timestamp in milliseconds (little-endian), and the rest is the payload.

| Type byte | Stream | Payload | Sent when |
| --- | --- | --- | --- |
| 1 | Colour | JPEG, 640x480 | Browser subscribed |
| 2 | Depth view | JPEG, colourised | Browser subscribed |
| 3 | Depth raw | 16-bit depth in mm, 320x240, little-endian | Point cloud view open |
| 4 | Fusion preview | JPEG of the shaded model | Scan running |

**JSON messages (bridge to browser)**

| type | Contents |
| --- | --- |
| status | Sensor state, real or mock mode, tilt angle, accelerometer, fps per stream |
| skeletons | Up to 2 tracked bodies: id, and per joint the position in metres, image position, and tracking state |
| mocap | Recording state, countdown, take list changes |
| fusion | Scan state, tracking OK or lost, current preset |
| log | A new log line |
| error | An error code and a plain-English message |

**JSON messages (browser to bridge)**

| type | Contents |
| --- | --- |
| subscribe | The streams the current tab needs |
| tilt | Target angle in degrees |
| skeleton.settings | Standing or seated, smoothing preset |
| mocap.start, mocap.stop | Start or stop a take |
| fusion.start, fusion.pause, fusion.reset, fusion.preset | Scan controls |
| export | Scan or take id and format. The reply holds a download link |

**Plain HTTP.** `GET /` serves the web folder. `GET /captures/...` downloads saved files and must refuse any path outside the captures folder.

**Slow browser rule.** The bridge keeps only the newest frame per stream and drops older ones. It never builds a queue.

*Plain English:* the subscribe message is like IGMP. The browser joins only the streams it is watching, so a hidden tab costs nothing. The slow browser rule is a queue depth of one: under congestion, the newest frame wins and latency stays low.

## Web interface

One page with a fixed header, a row of tabs, a large main view, and a control panel on the right. It is built for a desktop browser (Edge or Chrome) at 1280 px wide or more, and follows the Windows light or dark setting.

The header holds the app name, the status light, the tilt slider, and a "Mock mode" badge when the fake sensor is in use.

| Tab | Main view | Control panel |
| --- | --- | --- |
| Live | Colour and depth images, or the 3D point cloud | View toggle, people highlight, snapshot, fps |
| Skeleton | Skeleton over the image, or the 3D skeleton | Standing or seated, smoothing, overlay on colour or depth |
| Motion capture | 3D skeleton, live or playing a take | Record, take list, playback controls, export BVH or JSON |
| 3D scan | Live shaded model, then the finished mesh in 3D | Preset, start, pause, reset, export STL, OBJ or PLY |
| Status | Sensor details, accelerometer, stream rates | Log viewer, reconnect button |

Switching tabs sends a new subscribe message, so only the current tab's streams flow. Buttons that cannot work right now (for example, Record with no person tracked) are greyed out with a tooltip saying why.

## Build phases

Seven phases, built in order. At the end of each, Claude Code commits to Git, stops, and writes Steven a plain-English summary: what was built, how to test it, and what comes next. Steven ticks the checks on real hardware before the next phase starts.

### Phase 0: Setup

Claude Code installs the build tools, finds the Kinect library files on disk, checks the graphics card for DirectX 11, and writes docs/SETUP.md.

- [ ] Steven's hardware check (Environment and hardware section) passes
- [ ] `dotnet --version` and `git --version` both work
- [ ] SETUP.md records the paths of the Kinect and Fusion library files

### Phase 1: Bridge and mock sensor

The bridge starts, serves the page, opens the WebSocket, and reports status. Mock mode is built here so later phases can be developed without the Kinect.

- [ ] Double-clicking `run.cmd` starts the bridge and opens the page in the browser
- [ ] Status light is green with the Kinect, amber with `--mock`, red with it unplugged
- [ ] Unplugging and replugging the Kinect recovers within 5 seconds without a restart
- [ ] The page does not load from another device on the home network

### Phase 2: Live view

- [ ] Colour and depth both show at 25 fps or more
- [ ] A hand wave appears on screen with no visible lag
- [ ] Snapshot saves two PNG files to captures/snapshots
- [ ] The point cloud can be orbited with the mouse
- [ ] Tilt moves the Kinect and ignores requests faster than the rate limit

### Phase 3: Skeleton tracking

- [ ] The overlay lines up with Steven's body on both colour and depth
- [ ] Two people are tracked at once, drawn in different colours
- [ ] Seated mode tracks the upper body while sitting at the desk
- [ ] Changing smoothing visibly changes the jitter

### Phase 4: Motion capture

- [ ] A 30-second take records, saves as JSON, and plays back with the scrub bar
- [ ] The BVH export imports into Blender and the armature moves like Steven did
- [ ] Takes can be renamed and deleted from the page

### Phase 5: 3D scanning

- [ ] A scan of a chair or a person exports as STL and opens in Windows 3D Viewer
- [ ] OBJ and PLY exports open in Blender
- [ ] Moving the Kinect too fast shows the tracking-lost warning, and reset recovers
- [ ] Tuned preset values are saved in settings.json

### Phase 6: Polish and handover

- [ ] README explains how to start, use and stop the app in plain English
- [ ] Every fault message in the page says what to check
- [ ] Log files are capped in size and old ones deleted
- [ ] Claude Code walks Steven through the code, one folder at a time

## Requirements, risks and working rules

### Non-functional requirements

- **Local only:** the server binds to 127.0.0.1. No outbound network calls and no telemetry at run time.
- **No admin rights** needed to run the app once setup is done.
- **Fault isolation:** an error in one feature is logged and shown on its tab, and the rest of the app keeps running.
- **Low latency:** the newest-frame rule applies to every stream, and the PC stays responsive while the app runs.
- **Storage:** everything in captures stays out of Git.

### Known risks

| Risk | Effect | Mitigation |
| --- | --- | --- |
| SDK 1.8 was built for Windows 7 and 8 | Driver install or detection fails on Windows 11 | Phase 0 hardware check catches it early. Reinstall the SDK, try another port |
| Some USB 3 controllers handle the Kinect badly | Dropped frames or disconnects | Use a USB 2 port |
| Microsoft.Kinect is a .NET Framework library | Will not load on modern .NET | Target net481 (.NET Framework 4.8.1), as specified |
| No DirectX 11 graphics card | Fusion runs slowly on the CPU | CPU fallback with a warning and a smaller preset |
| BVH axis or rotation order differs from Blender's | Imported skeleton twists or faces the wrong way | Test with a T-pose take in Phase 4. Add an axis option if needed |
| Windows needs a URL reservation for the server | App fails to start | One-time command documented in SETUP.md |
| Another program is using the Kinect | Bridge cannot open the sensor | Red status with a message to close other Kinect apps |
| Sunlight or other infrared sources | Noisy or missing depth | Noted in the README |

### Working rules for Claude Code

1. Read SPEC.md before starting. Build one phase at a time and stop at the end of each.
2. Run natively on Windows in PowerShell or Git Bash, never in WSL.
3. Steven is not a programmer. Explain every change in plain English, and use networking analogies where they help.
4. Comment the code to explain why, not just what. Keep one job per file.
5. Add no frameworks, packages or services beyond this spec without asking first.
6. Develop against mock mode. Mark every check that needs the real Kinect as Steven's to test.
7. If the spec is wrong or unclear, stop and ask. Record the agreed change in SPEC.md.
8. Written docs use plain English, no em-dashes, and describe the current state only.

**First prompt for Claude Code:** "Read SPEC.md, then complete Phase 0 only and stop with a plain-English summary."
