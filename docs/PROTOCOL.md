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

`error` codes: `badMessage` (not valid JSON, no `type`, or the handler failed), `badVersion`, `unknownType`, `badTilt` (no angle), `tiltRefused` (motor limits, or the sensor is not ready; the message says when to try again), `snapshotFailed`, `badSetting` (a skeleton setting that is not one of the allowed values).

### Browser to bridge

| type | Contents | What the bridge does |
| --- | --- | --- |
| `subscribe` | `streams`: list of stream names | Records which streams this tab wants. Known names: `colour`, `depth`, `depthRaw`, `skeletons`, `fusion`. Unknown names are ignored |
| `sensor.reconnect` | none | Lets go of the sensor and opens it again (the Reconnect button) |
| `tilt` | `angle`: degrees, -27 to 27 (rounded, and clamped to that range) | Moves the tilt motor, unless it moved less than 1 second ago, has moved 15 times in the last 20 seconds, or is still moving. Refusals come back as a `tiltRefused` error. Requests are refused, never queued |
| `live.settings` | `peopleHighlight`: true or false | Greys out everything except tracked people in the depth view and depth snapshots |
| `skeleton.settings` | `mode`: `standing` or `seated`, and/or `smoothing`: `off`, `light` or `heavy`. Either may be left out | Changes skeleton tracking for everyone and sends a fresh `status`. Changing smoothing pauses tracking for under a second while the Kinect picks people up again |
| `snapshot` | none | Saves the newest colour and depth pictures as PNG files in `captures/snapshots`, named `snapshot-YYYYMMDD-HHMMSS-colour.png` and `-depth.png`. Replies with a `snapshot` message |

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
- `id` is the SDK's tracking number, stable while the person stays in view. `player` (1 to 6) matches the person number in the depth picture, and picks the person's colour on the page.
- `joints` is keyed by joint name: `hipCenter`, `spine`, `shoulderCenter`, `head`, `shoulderLeft`, `elbowLeft`, `wristLeft`, `handLeft`, `shoulderRight`, `elbowRight`, `wristRight`, `handRight`, `hipLeft`, `kneeLeft`, `ankleLeft`, `footLeft`, `hipRight`, `kneeRight`, `ankleRight`, `footRight`. Joints the Kinect is not tracking at all are left out; in seated mode only the 10 from `head` to `handRight` appear.
- `p` is the position in metres from the Kinect: X sideways (positive is to the right in the pictures), Y up, Z straight out from the sensor. Rounded to the millimetre.
- `colour` and `depth` are the joint's pixel position in the 640x480 colour and depth pictures, from the SDK's coordinate mapper, which allows for the gap between the two cameras.
- `state` is `tracked` (seen) or `inferred` (guessed, for example a hand hidden behind the body).
- `floor` is the floor plane `[A, B, C, D]`, where `Ax + By + Cz + D = 0` in the same coordinates, or `null` if the Kinect cannot see the floor. `D` is roughly the Kinect's height above the floor in metres.
