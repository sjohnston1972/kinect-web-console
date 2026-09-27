// The 3D point cloud: every depth reading drawn as a coloured dot in its real position, in metres.
// Loaded as a module because Three.js is one. Uses Connection, DepthColours and LiveStats from the other files.

import { createView, THREE } from './scene3d.js';

const W = 320;
const H = 240;
const FOCAL = 285.63;   // the Kinect depth camera's focal length in pixels at 320x240, from Microsoft's SDK
const HIDDEN_Y = -1000; // points with no reading are parked here, far outside the view, instead of at the camera

let view = null;
let geometry, positions, colours;
let waiting = null;   // newest depth frame not yet applied

// How far left/right and up/down each pixel's direction points, per metre of distance
const rayX = new Float32Array(W);
const rayY = new Float32Array(H);
for (let u = 0; u < W; u++) rayX[u] = (u - W / 2 + 0.5) / FOCAL;
for (let v = 0; v < H; v++) rayY[v] = -(v - H / 2 + 0.5) / FOCAL;

function build() {
  view = createView();

  positions = new Float32Array(W * H * 3);
  colours = new Float32Array(W * H * 3);
  for (let i = 0; i < W * H; i++) positions[i * 3 + 1] = HIDDEN_Y;
  geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3).setUsage(THREE.DynamicDrawUsage));
  geometry.setAttribute('color', new THREE.BufferAttribute(colours, 3).setUsage(THREE.DynamicDrawUsage));
  const points = new THREE.Points(geometry, new THREE.PointsMaterial({ size: 0.012, vertexColors: true }));
  points.frustumCulled = false;   // the points move every frame, so skip the bounds check
  view.scene.add(points);

  view.beforeRender = () => {
    if (!waiting) return;
    applyDepth(waiting);
    waiting = null;
  };
}

function applyDepth(frame) {
  // The payload starts at an odd byte, so copy it before reading it as 16-bit numbers
  const depth = new Uint16Array(frame.buffer.slice(Connection.HEADER_BYTES));
  const lookup = DepthColours.lookup;
  for (let v = 0, i = 0; v < H; v++) {
    for (let u = 0; u < W; u++, i++) {
      const mm = depth[i];
      const p = i * 3;
      if (mm === 0 || mm >= 8192) {
        positions[p] = 0;
        positions[p + 1] = HIDDEN_Y;
        positions[p + 2] = 0;
        continue;
      }
      const z = mm / 1000;
      positions[p] = rayX[u] * z;
      positions[p + 1] = rayY[v] * z;
      positions[p + 2] = -z;
      colours[p] = lookup[mm * 3];
      colours[p + 1] = lookup[mm * 3 + 1];
      colours[p + 2] = lookup[mm * 3 + 2];
    }
  }
  geometry.attributes.position.needsUpdate = true;
  geometry.attributes.color.needsUpdate = true;
  LiveStats.tick('depthRaw', frame.timestamp);
}

function show(element) {
  if (!view) build();
  view.show(element);
}

function hide() {
  waiting = null;
  if (view) view.hide();
}

Connection.on('stream:3', (frame) => {
  if (view && view.running) waiting = frame;
});

window.PointCloud = { show, hide };
if (window.onPointCloudReady) window.onPointCloudReady();
