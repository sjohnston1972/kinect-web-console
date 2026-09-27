# Kinect Web Console

Use an Xbox 360 Kinect from a web page on this PC. The page shows live colour and depth pictures, a 3D point cloud, and the Kinect's status, and lets you tilt the Kinect and save snapshots. It also tracks skeletons. Motion capture and 3D scanning follow in later phases.

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
