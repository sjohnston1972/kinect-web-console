// The 3D point cloud: every depth reading drawn as a coloured dot in its real position, in metres.
// Drag to orbit, scroll to zoom, right-drag to pan, double-click to go back to the Kinect's own view.
// Loaded as a module because Three.js is one. Uses Connection, DepthColours and LiveStats from the other files.

import * as THREE from 'three';
import { OrbitControls } from '../vendor/three/OrbitControls.js';

const W = 320;
const H = 240;
const FOCAL = 285.63;   // the Kinect depth camera's focal length in pixels at 320x240, from Microsoft's SDK
const HIDDEN_Y = -1000; // points with no reading are parked here, far outside the view, instead of at the camera
const KINECT_VIEW = { position: [0, 0.9, 1.4], target: [0, 0, -2.2] };   // a little above and behind the Kinect

let renderer = null;
let scene, camera, controls, geometry, positions, colours;
let container = null;
let running = false;
let resizeWatcher = null;
let waiting = null;   // newest depth frame not yet applied

// How far left/right and up/down each pixel's direction points, per metre of distance
const rayX = new Float32Array(W);
const rayY = new Float32Array(H);
for (let u = 0; u < W; u++) rayX[u] = (u - W / 2 + 0.5) / FOCAL;
for (let v = 0; v < H; v++) rayY[v] = -(v - H / 2 + 0.5) / FOCAL;

function build() {
  renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);

  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x101216);

  camera = new THREE.PerspectiveCamera(58, 4 / 3, 0.05, 30);
  camera.position.set(...KINECT_VIEW.position);

  controls = new OrbitControls(camera, renderer.domElement);
  controls.target.set(...KINECT_VIEW.target);
  controls.enableDamping = true;
  controls.maxDistance = 12;
  controls.update();
  controls.saveState();
  renderer.domElement.addEventListener('dblclick', () => controls.reset());

  positions = new Float32Array(W * H * 3);
  colours = new Float32Array(W * H * 3);
  for (let i = 0; i < W * H; i++) positions[i * 3 + 1] = HIDDEN_Y;
  geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3).setUsage(THREE.DynamicDrawUsage));
  geometry.setAttribute('color', new THREE.BufferAttribute(colours, 3).setUsage(THREE.DynamicDrawUsage));
  const points = new THREE.Points(geometry, new THREE.PointsMaterial({ size: 0.012, vertexColors: true }));
  points.frustumCulled = false;   // the points move every frame, so skip the bounds check
  scene.add(points);

  // A small box where the Kinect is, so it is clear which way the cloud faces
  const kinect = new THREE.Mesh(new THREE.BoxGeometry(0.2, 0.05, 0.05), new THREE.MeshBasicMaterial({ color: 0x5d6673 }));
  scene.add(kinect);
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

function fit() {
  const width = container.clientWidth;
  const height = container.clientHeight;
  if (width === 0 || height === 0) return;
  renderer.setSize(width, height, false);
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
}

// One loop draws the scene; depth frames are applied here too, so at most one per screen refresh
function loop() {
  if (!running) return;
  if (waiting) {
    applyDepth(waiting);
    waiting = null;
  }
  controls.update();
  renderer.render(scene, camera);
  requestAnimationFrame(loop);
}

function show(element) {
  if (running && container === element) return;
  if (!renderer) build();
  container = element;
  container.append(renderer.domElement);
  resizeWatcher = new ResizeObserver(fit);
  resizeWatcher.observe(container);
  fit();
  running = true;
  requestAnimationFrame(loop);
}

function hide() {
  running = false;
  waiting = null;
  if (resizeWatcher) resizeWatcher.disconnect();
}

Connection.on('stream:3', (frame) => {
  if (running) waiting = frame;
});

window.PointCloud = { show, hide };
if (window.onPointCloudReady) window.onPointCloudReady();
