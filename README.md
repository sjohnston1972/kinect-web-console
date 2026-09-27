# Kinect Web Console

Use an Xbox 360 Kinect from a web page on this PC. The page shows live colour and depth pictures, a 3D point cloud, and the Kinect's status, and lets you tilt the Kinect and save snapshots. It also tracks skeletons, records motion capture for Blender, and builds 3D scans with Kinect Fusion.

## Start it

- **With the Kinect:** double-click `run.cmd`.
- **Without the Kinect (fake sensor for testing):** double-click `run-mock.cmd`.

A black window opens, builds the app (a few seconds), then opens the page at http://localhost:8766 in your browser. Keep the black window open while you use the app.

## Stop it

Close the black window, or click in it and press Ctrl+C.

## The Live tab

- **View:** colour and depth side by side, either one on its own, or **3D points** (drag to orbit, scroll to zoom, right-drag to pan, double-click to reset).
- **Depth colours:** near is red and orange, far is green and blue. The scale under the depth picture is in metres. Black means no reading: too close (under 0.8 m), too far, or a shiny or dark surface.
- **Highlight people:** everything except people the Kinect is tracking turns grey. Step fully into view for the Kinect to pick you up.
- **Take snapshot:** saves the colour and depth pictures as PNG files in `captures\snapshots`. Links to them appear under the button.
- **Rates:** pictures per second reaching the page, and how old each picture is when it arrives.

**Tilt:** drag the slider in the top right and let go. The Kinect moves at most once a second and 15 times in 20 seconds, to protect its motor; the page says if it has to wait.

## The Skeleton tab

- Stand **1.5 to 3.5 m** from the Kinect, facing it, with your whole body in view. Moving your arms helps it pick you up. Up to 2 people are tracked at once, each in their own colour.
- **Over picture** draws the skeleton on the colour or depth picture. **3D** shows it on its own, standing on the floor the Kinect detects (drag to orbit, as with the 3D points).
- **Standing** tracks all 20 joints. **Seated** tracks the 10 upper-body joints, for sitting at a desk 1.2 to 2 m from the Kinect.
- **Smoothing** steadies jittery joints: Off is quickest but shaky, Heavy is very steady but trails fast movement. Changing it pauses tracking for under a second.
- Faded, dashed joints are ones the Kinect is guessing, such as a hand hidden behind your body.
- Too close (under about 1.2 m), or the Kinect looking up at you steeply, and it will not track you. Tilt it level and step back.

## The Motion capture tab

1. Click **Record**. You get a 3-second countdown to step into position (2 to 3 m away, whole body in view).
2. Move. The view shows your skeleton and a red REC timer.
3. Click **Stop**. The take is saved in `captures\mocap` and appears in the list, named by date and time.

- Click a take to **play** it. Use the slider to scrub, **Pause** to hold a pose, and **Back to live view** to return.
- **Rename** and **Delete** are next to each take. Deleted takes go to the Recycle Bin.
- **Export BVH** makes a file for Blender: File, Import, Motion Capture (.bvh), default settings. The skeleton stands in the middle of Blender's floor, facing the front view. To find it: click it in the Outliner (top right), then View, Frame Selected. Orbit by dragging with the middle mouse button, pan with Shift and middle-drag, zoom with the scroll wheel, and play with the spacebar. It holds one person and needs a take recorded in Standing mode. Starting a take with a T-pose (arms straight out) makes it easy to line up in Blender.
- **Download JSON** gives the take exactly as recorded, every joint of every frame.

## The 3D scan tab

1. Pick a **preset**: **Object** (a 1 m box, 2 mm detail, starting 0.6 m away, centred on where the Kinect points), **Person** (1.5 m wide and 2 m tall, 4 mm detail, starting 1 m away, standing on the floor: also right for a chair) or **Room** (4 m by 3 m by 4 m, 8 mm detail).
2. Tick **Capture colour** if you want colours in the PLY and OBJ files.
3. Aim using the depth picture: the **In range** bar should be well into the green. If it says you are too close, step back (the Kinect cannot measure anything nearer than 0.8 m).
4. Press **Start**. The picture changes to the model building up, shaded, from the Kinect's point of view.
5. Keep what you are scanning still and move the Kinect slowly around it. Turning the thing instead (a person on a swivel chair) only works when nothing else is within range, because Kinect Fusion works out movement from the whole scene.
6. Press **Pause** when it looks complete, then **STL**, **OBJ** or **PLY** to save it in `captures\scans`, or **View in 3D** to look at it on the page.

- If the picture shows **Tracking lost**, the Kinect moved too fast. Move it back to where the picture last looked right and hold still, or press **Reset** to start again.
- **STL** opens in Windows 3D Viewer (double-click the file). **OBJ** and **PLY** open in Blender: File, Import, Wavefront (.obj) or Stanford PLY (.ply).
- Changing preset or pressing Reset clears the scan, so export first if you want to keep it.
- The presets' sizes and detail are in `settings.json` under `fusion`.

## The status light

The light in the top left of the page:

| Light | Meaning |
| --- | --- |
| Green | The Kinect is ready |
| Amber | The Kinect is starting up, or the app is in mock mode (fake sensor) |
| Red | Something needs attention. The Status tab says what to check |

The Status tab also shows the tilt angle, whether the Kinect is level, the bridge log, and a Reconnect button.

## Good to know

- The page only works on this PC. Other devices on your network are refused.
- Only one program can use the Kinect at a time. Close the Microsoft sample apps before starting this one.
- If VMware is running, it can take the Kinect. `docs/SETUP.md` explains the fix.
- Sunlight and other infrared sources make the depth picture noisy.
- Settings (port, captures folder, picture quality) live in `settings.json`.
- Logs are in the `logs` folder, capped at 1 MB per file with the three most recent older files kept.
- `tools\look.ps1` grabs what the Kinect sees right now (colour and depth pictures in `captures\look`, plus a summary of distances and people). It lets someone help check a set-up remotely. Run it while the bridge is running: `powershell -File tools\look.ps1`.
