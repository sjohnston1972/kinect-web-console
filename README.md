# Kinect Web Console

Use an Xbox 360 Kinect from a web page on this PC. The page shows the Kinect's status, and in later phases live video and depth, skeleton tracking, motion capture and 3D scanning.

## Start it

- **With the Kinect:** double-click `run.cmd`.
- **Without the Kinect (fake sensor for testing):** double-click `run-mock.cmd`.

A black window opens, builds the app (a few seconds), then opens the page at http://localhost:8766 in your browser. Keep the black window open while you use the app.

## Stop it

Close the black window, or click in it and press Ctrl+C.

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
