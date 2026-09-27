# Protocol

How the browser page and the bridge talk. This file lists only what the code does now; each phase adds its messages here.

## Addresses

| Address | What it does |
| --- | --- |
| `http://localhost:8766/` | Serves files from the `web` folder. `/` serves `web/index.html` |
| `http://localhost:8766/captures/...` | Downloads a file from the captures folder. Any path that leads outside the captures folder gets 404 |
| `ws://localhost:8766/ws` | The WebSocket that carries all live data |

The port comes from `settings.json`.

**Local only.** Windows turns away requests addressed to this PC's network IP. Anything that still arrives from another address gets 403. The WebSocket also refuses pages from any other website (it checks the `Origin` header), so a web page elsewhere cannot reach the Kinect through your browser.

## JSON messages

Every JSON message is a text WebSocket message with a `type` and a protocol version `v: 1`. A message with a different version gets a `badVersion` error.

### Bridge to browser

| type | Sent when | Contents |
| --- | --- | --- |
| `status` | On connect, when the sensor state changes, and once a second | See below |
| `log` | On connect (the last 50 lines), then each new line | `time` (HH:mm:ss), `level` (info, warn, error), `text` |
| `error` | The browser sent something the bridge could not act on | `code`, `message` (plain English) |
| `snapshot` | Reply to a `snapshot` request | `files`: list of `{ name, url }` for the colour and depth PNG files |
| `skeletons` | About 30 times a second while subscribed to `skeletons`. Newest-only | See below |
| `mocap` | On connect, 4 times a second during a countdown or recording, and when anything changes | See below |
| `fusion` | On connect; 4 times a second while a page is subscribed to `fusion` or a scan is running; otherwise every 2 seconds without the coverage measurement | See below |
| `export` | Reply to an `export` request | `kind`, `id`, `format`, `name` (file name), `url` (download link), and for BVH a `note` with the frame count and accuracy |

`status` contents:

```json
{
  "type": "status", "v": 1,
  "mock": false,
  "sensor": {
    "state": "ready",
    "light": "green",
    "title": "Kinect ready",
    "help": "",
    "detail": "USB\\VID_0409&PID_005A\\..."
  },
  "tilt": { "angle": 18, "moving": false },
  "accelerometer": { "x": -0.035, "y": -0.943, "z": -0.31, "sideTilt": -2.2, "frontTilt": 18.2 },
  "fps": { "colour": 30.0, "depth": 30.0 },
  "live": { "peopleHighlight": false },
  "skeleton": { "mode": "standing", "smoothing": "light" },
  "uptimeSeconds": 27
}
```

- `state` is one of `initialising`, `ready`, `noSensor`, `notPowered`, `inUse`, `badUsb`, `error`.
- `light` is `green` (ready), `amber` (starting, or any time in mock mode) or `red` (a fault). `title` and `help` are the plain-English words the page shows.
- `tilt` and `accelerometer` are `null` when there is no working sensor. Accelerometer values are in units of gravity. `sideTilt` and `frontTilt` are degrees from level; `frontTilt` has the same sign as the tilt motor (up is positive).
- `tilt.moving` is true while the motor is moving.
- `fps` is frames per second arriving from the sensor, per stream (`colour`, `depth`, `skeletons`). Empty when the sensor is not ready.
- `skeleton` is the current tracking mode (`standing` or `seated`) and smoothing (`off`, `light`, `heavy`), shared by every open page.
- `live.peopleHighlight` is the current people highlight setting, shared by every open page.

Status is sent "newest only": if a browser falls behind, an unsent older status is replaced by the newer one.

`error` codes: `badMessage` (not valid JSON, no `type`, or the handler failed), `badVersion`, `unknownType`, `badTilt` (no angle), `tiltRefused` (motor limits, or the sensor is not ready; the message says when to try again), `snapshotFailed`, `badSetting` (a setting that is not one of the allowed values), `mocapRefused` (already recording, or the Kinect is not ready), `noSuchTake`, `badName`, `badExport`, `exportFailed`, `fusionRefused` (the Kinect is not ready, or Kinect Fusion could not start).

### Browser to bridge

| type | Contents | What the bridge does |
| --- | --- | --- |
| `subscribe` | `streams`: list of stream names | Records which streams this tab wants. Known names: `colour`, `depth`, `depthRaw`, `skeletons`, `fusion`. Unknown names are ignored |
| `sensor.reconnect` | none | Lets go of the sensor and opens it again (the Reconnect button) |
| `tilt` | `angle`: degrees, -27 to 27 (rounded, and clamped to that range) | Moves the tilt motor, unless it moved less than 1 second ago, has moved 15 times in the last 20 seconds, or is still moving. Refusals come back as a `tiltRefused` error. Requests are refused, never queued |
| `live.settings` | `peopleHighlight`: true or false | Greys out everything except tracked people in the depth view and depth snapshots |
| `skeleton.settings` | `mode`: `standing` or `seated`, and/or `smoothing`: `off`, `light` or `heavy`. Either may be left out | Changes skeleton tracking for everyone and sends a fresh `status`. Changing smoothing pauses tracking for under a second while the Kinect picks people up again |
| `mocap.start` | none | Starts the 3-second countdown, then records every skeleton frame. Refused while already recording or when the Kinect is not ready. Starts even with nobody tracked, so there is time to step into view |
| `mocap.stop` | none | Stops and saves the take, or cancels a countdown |
| `mocap.rename` | `id`, `name` | Renames the take; the file is renamed to match (unsafe characters become dashes). Any BVH made under the old name is removed |
| `mocap.delete` | `id` | Moves the take and its BVH to the Recycle Bin |
| `export` | `kind`: `take`, `id`, `format`: `bvh` or `json`; or `kind`: `scan`, `format`: `stl`, `obj`, `ply` or `preview` | Replies with a download link. Exports run in the background, so the page's other messages carry on meanwhile, and only one runs at a time (a second gets `exportFailed`). BVH files are made on request and saved next to the take. Scan files are made from the model as it is now (scanning pauses only while the mesh is read out); `preview` makes a lighter PLY in `captures/scans/preview` for the page's 3D view |
| `fusion.start` | none | Starts scanning, or carries on after a pause. Builds the Fusion volume for the current preset on first use |
| `fusion.pause` | none | Stops merging frames; the model is kept |
| `fusion.reset` | none | Clears the model and starts again from where the Kinect is now. Keeps scanning if it was |
| `fusion.preset` | `preset`: `object`, `person` or `room` | Switches preset. Clears the model, because the volume is rebuilt at the new size |
| `fusion.colour` | `on`: true or false | Colour capture: colours go into the model, and into PLY and OBJ exports |
| `snapshot` | none | Saves the newest colour and depth pictures as PNG files in `captures/snapshots`, named `snapshot-YYYYMMDD-HHMMSS-mmm-colour.png` and `-depth.png` (to the millisecond, so none overwrite). Replies with a `snapshot` message |

The page sends `subscribe` on every connect, every tab switch, and every change of view on the Live tab.

## Sending rules

- Messages that must all arrive (`log`, `error`) go in order, capped at 500 waiting per browser.
- Everything else is newest-only per stream: the bridge never builds a queue of old frames for a slow browser.
- Incoming messages over 64 KB close the connection.
- Windows' own network buffers sit below the bridge and can hold about 300 KB (roughly 12 pictures). A page that stops reading for a while gets those few old pictures first when it resumes, all within a moment, then fresh ones. The page only draws the newest picture waiting, so this does not show on screen.

## Binary messages (bridge to browser)

Pictures and depth travel as binary WebSocket messages. Every one starts with a 9-byte header:

| Bytes | Meaning |
| --- | --- |
| 0 | Stream type (below) |
| 1 to 8 | When the frame arrived from the sensor, in milliseconds since 1 January 1970 (UTC), little-endian |
| 9 onwards | The payload |

| Type | Stream name (for `subscribe`) | Payload | Sent when |
| --- | --- | --- | --- |
| 1 | `colour` | JPEG, 640x480 | Subscribed |
| 2 | `depth` | JPEG, 640x480, coloured by distance: near is red and orange, far is green and blue, over 0.8 m to 4 m. Dimmed outside that range, near-black where there is no reading | Subscribed |
| 3 | `depthRaw` | 320x240 distances, 16-bit little-endian, in millimetres, row by row from the top left. 0 means no reading. Every second pixel of the full depth picture in each direction | Subscribed |
| 4 | `fusion` | JPEG, 640x480: the 3D scan so far, shaded (or in colour once colour has been captured), seen from where the Kinect is now. About 15 a second while scanning | Subscribed, while scanning, and once on pause |

JPEG quality comes from `streamQuality.jpegQuality` in `settings.json` (80 by default).

The depth colours are defined twice and must match: `bridge/Streams/DepthColouriser.cs` paints the pictures, and `web/js/depthcolours.js` draws the legend and colours the point cloud.

## The skeletons message

```json
{
  "type": "skeletons", "v": 1,
  "timestamp": 1790000000000,
  "floor": [0.018, 0.999, -0.031, 0.747],
  "bodies": [
    {
      "id": 3, "player": 1,
      "joints": {
        "head": { "p": [0.08, 0.95, 2.56], "colour": [338.0, 55.0], "depth": [338.0, 27.0], "state": "tracked" },
        "handLeft": { "p": [-0.16, 0.22, 3.01], "colour": [292.0, 211.0], "depth": [290.0, 198.0], "state": "inferred" }
      }
    }
  ]
}
```

- `bodies` holds the fully tracked people: up to 2 on the Xbox 360 Kinect. Empty when nobody is tracked.
- `id` is the SDK's tracking number, stable while the person stays in view. `player` (1 to 6) is the SDK's own number, which matches the person number in the depth picture. `person` is 1, 2, ... in the order people appeared (the lowest number not in use), kept while they stay tracked; bodies are listed in `person` order, and the page colours and labels people by it, so one person alone is always Person 1 in red.
- `joints` is keyed by joint name: `hipCenter`, `spine`, `shoulderCenter`, `head`, `shoulderLeft`, `elbowLeft`, `wristLeft`, `handLeft`, `shoulderRight`, `elbowRight`, `wristRight`, `handRight`, `hipLeft`, `kneeLeft`, `ankleLeft`, `footLeft`, `hipRight`, `kneeRight`, `ankleRight`, `footRight`. Joints the Kinect is not tracking at all are left out; in seated mode only the 10 from `head` to `handRight` appear.
- `p` is the position in metres from the Kinect: X sideways (positive is to the right in the pictures), Y up, Z straight out from the sensor. Rounded to the millimetre.
- `colour` and `depth` are the joint's pixel position in the 640x480 colour and depth pictures, from the SDK's coordinate mapper, which allows for the gap between the two cameras.
- `state` is `tracked` (seen) or `inferred` (guessed, for example a hand hidden behind the body).
- `floor` is the floor plane `[A, B, C, D]`, where `Ax + By + Cz + D = 0` in the same coordinates, or `null` if the Kinect cannot see the floor. `D` is roughly the Kinect's height above the floor in metres.

## The mocap message

```json
{
  "type": "mocap", "v": 1,
  "state": "recording",
  "countdown": 0,
  "elapsed": 12.4,
  "frames": 371,
  "peopleNow": 1,
  "takes": [
    { "id": "take-20260927-102921", "name": "Take 2026-09-27 10:29:21", "created": "2026-09-27 10:29:21",
      "duration": 45.168, "frames": 1355, "people": 1, "mode": "standing" }
  ],
  "event": { "kind": "saved", "id": "take-20260927-102921", "name": "Take 2026-09-27 10:29:21" }
}
```

- `state` is `idle`, `countdown`, `recording` or `saving`. `countdown` is the seconds left (3, 2, 1). `elapsed` and `frames` count the take so far. `peopleNow` is how many people are tracked at this moment.
- `takes` (newest first) is included on connect and whenever the list changes. `id` is the file name without `.json`.
- `event` appears once when something happens: `saved` (`id`, `name`), `failed` (`message`: for example nobody was tracked, so nothing was saved), `renamed` (`oldId`, `id`, `name`), `deleted` (`id`).
- Plain state updates are newest-only; messages carrying `takes` or an `event` always arrive.
- Recording stops by itself after 10 minutes.

## Take files

Each take is `captures/mocap/<id>.json`, and the page loads it from `/captures/mocap/<id>.json` for playback. `captures/mocap/.takes-index.json` lists each take's details with its file size and time, so start-up only reads a take in full when it has changed; it is rebuilt automatically if deleted.

```json
{
  "format": "kinect-web-console-take", "version": 1,
  "name": "Take 2026-09-27 10:29:21", "created": "2026-09-27 10:29:21",
  "mode": "standing", "smoothing": "light",
  "durationSeconds": 45.168, "frameCount": 1355, "people": 1,
  "floor": [0.016, 0.996, 0.087, 0.743],
  "frames": [
    { "t": 33, "bodies": [
      { "id": 5, "player": 1, "joints": {
        "head": { "p": [0.08, 0.95, 2.56], "state": "tracked", "rot": [0.01, 0.02, 0.0, 0.9997] }
      } } ] }
  ]
}
```

- Every skeleton frame is kept (not just the newest), with `t` in milliseconds from the start of recording. `bodies` is empty in frames where nobody was tracked.
- Joints use the same names and coordinates as the `skeletons` message, rounded to a tenth of a millimetre.
- `rot` is the SDK's hierarchical bone orientation for the bone ending at that joint, as a quaternion `[x, y, z, w]`: the bone's rotation relative to the bone it hangs from, with every bone pointing along its own +Y. For `hipCenter` it is the whole body's rotation relative to the Kinect. The mock sensor works these out from the joint positions the same way.

## BVH files

`captures/mocap/<id>.bvh`, made by `export` with `format: "bvh"`. For Blender: File, Import, Motion Capture (.bvh), default settings.

- One person: the one tracked in the most frames. Standing takes only (seated takes have no hips or legs).
- Units are metres, Y up. The take is placed for Blender: levelled using the floor the Kinect detected (which undoes the Kinect's tilt), standing on the floor at height 0, centred on where the hips usually are, and turned so the person faces Blender's front view. Only the root is moved and turned, so the motion itself is unchanged.
- The root `Hips` sits at the hip centre, with position and rotation channels. Every other joint is one Kinect bone, named for the body part (`LowerSpine`, `UpperSpine`, `Neck`, `CollarLeft`, `UpperArmLeft`, `ForearmLeft`, `HandLeft`, `PelvisLeft`, `ThighLeft`, `ShinLeft`, `FootLeft`, and the same on the right). Each starts where its parent bone ends, along the parent's +Y, so its rotation is exactly the SDK's hierarchical rotation.
- Rotation channels are `Zrotation Xrotation Yrotation`. Bone lengths are each bone's middle length over the take.
- Frames are evened out to exactly 30 a second; a joint missing from a frame keeps its last rotation.
- The export rebuilds every joint from the BVH and reports the average distance from where the Kinect saw it (the `note` in the reply). About 3 cm is typical for a real take: the Kinect's own bone lengths wobble slightly from frame to frame, and the BVH uses fixed lengths.

## The fusion message

```json
{
  "type": "fusion", "v": 1,
  "state": "scanning",
  "preset": "room",
  "presets": [ { "name": "object", "label": "Object", "size": [0.75, 0.75, 0.75], "detailMm": 2.0, "startDistance": 0.8 } ],
  "tracking": "ok",
  "framesIntegrated": 145,
  "fps": 30.3,
  "colour": false,
  "processor": "Graphics card: AMD Radeon RX 7700 XT",
  "processorWarning": null,
  "error": null,
  "scanId": "scan-20260927-104552-room",
  "files": [ { "name": "scan-20260927-104552-room.stl", "sizeMb": 6.2, "url": "/captures/scans/scan-20260927-104552-room.stl" } ]
}
```

- `state` is `idle`, `scanning` or `paused`. `tracking` is `ok`, `lost` (8 depth frames in a row could not be lined up with the model) or `idle`.
- `framesIntegrated` is how many depth frames have been merged into this scan. `fps` is frames processed per second while scanning.
- `processor` says where Kinect Fusion runs. `processorWarning` is set when it had to fall back to the processor (slow, and at most 256 voxels per side). `error` explains an automatic pause, for example when the Kinect was unplugged.
- `presets` lists all three with their size in metres (width, height, depth), detail in millimetres per voxel, and the distance from the Kinect to the front of the scanned box.
- `placement` says where the box sits for this scan, in words (for example "standing on the floor, 0.74 m below the Kinect").
- `files` lists the 12 newest exports in `captures/scans`.
- `inRange` is the percentage of the newest depth picture inside the preset's scanning range; `tooClose` the percentage nearer than 0.8 m, which the Kinect cannot measure. `hint` is plain-English advice when less than 15% is in range, otherwise null.

## Scan files

`captures/scans/scan-YYYYMMDD-HHMMSS-<preset>.<stl|obj|ply>`; one scan can be exported in all three formats under the same name.

- Placed to open the right way up: unmirrored (the Kinect's depth picture is a mirror image), levelled using the accelerometer reading from when the scan started, standing on the ground (lowest point at 0), centred, and facing the front view.
- **STL:** binary, millimetres, Z up. For Windows 3D Viewer and 3D printing. No colour.
- **OBJ:** text, metres, Y up, shared vertices. With colour capture, each vertex line carries red, green and blue (0 to 1), which Blender reads.
- **PLY:** binary little-endian, metres, Z up, shared vertices, with red, green and blue bytes per vertex when colour was captured.
- The export detail is set by the preset's `meshVoxelStep` in `settings.json` (1 is full detail; 2 keeps a quarter of the triangles).

## Scan presets

In `settings.json` under `fusion.presets`, one entry each for `object`, `person` and `room`:

| Setting | Meaning |
| --- | --- |
| `voxelsPerMeter` | Detail: 512 is 2 mm cubes, 256 is 4 mm, 128 is 8 mm |
| `voxels` | Size of the box in voxels, width, height, depth (each rounded to a multiple of 32) |
| `startDistance` | Metres from the Kinect to the front of the box |
| `minDepth`, `maxDepth` | Depth readings outside this range (metres) are ignored |
| `meshVoxelStep` | Export detail, as above |
| `onFloor` | true to stand the box on the floor (using the floor the skeleton tracker sees, 5 cm below it), false to centre it on where the Kinect points |
| `label` | The name on the page |

Changes take effect when the bridge restarts.
