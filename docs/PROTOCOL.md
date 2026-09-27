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
  "tilt": { "angle": 18 },
  "accelerometer": { "x": -0.035, "y": -0.943, "z": -0.31, "sideTilt": -2.2, "frontTilt": 18.2 },
  "fps": {},
  "uptimeSeconds": 27
}
```

- `state` is one of `initialising`, `ready`, `noSensor`, `notPowered`, `inUse`, `badUsb`, `error`.
- `light` is `green` (ready), `amber` (starting, or any time in mock mode) or `red` (a fault). `title` and `help` are the plain-English words the page shows.
- `tilt` and `accelerometer` are `null` when there is no working sensor. Accelerometer values are in units of gravity. `sideTilt` and `frontTilt` are degrees from level; `frontTilt` has the same sign as the tilt motor (up is positive).
- `fps` is empty until streams exist.

Status is sent "newest only": if a browser falls behind, an unsent older status is replaced by the newer one.

`error` codes: `badMessage` (not valid JSON, or no `type`), `badVersion`, `unknownType`.

### Browser to bridge

| type | Contents | What the bridge does |
| --- | --- | --- |
| `subscribe` | `streams`: list of stream names | Records which streams this tab wants. Known names: `colour`, `depth`, `depthRaw`, `skeletons`, `fusion`. Unknown names are ignored. No streams are sent yet |
| `sensor.reconnect` | none | Lets go of the sensor and opens it again (the Reconnect button) |

The page sends `subscribe` on every connect and every tab switch.

## Sending rules

- Messages that must all arrive (`log`, `error`) go in order, capped at 500 waiting per browser.
- Everything else is newest-only per stream: the bridge never builds a queue of old frames for a slow browser.
- Incoming messages over 64 KB close the connection.
