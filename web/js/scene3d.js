// A reusable 3D view: a Three.js scene with a camera the mouse can orbit
// (drag to orbit, scroll to zoom, right-drag to pan, double-click to reset).
// Used by the point cloud and the 3D skeleton. Coordinates are the Kinect's, in metres:
// the Kinect sits at the origin looking down -Z, Y is up.

import * as THREE from 'three';
import { OrbitControls } from '../vendor/three/OrbitControls.js';

// A little above and behind the Kinect, looking into the room
const DEFAULT_VIEW = { position: [0, 0.9, 1.4], target: [0, 0, -2.2] };

export function createView({ background = 0x101216, view = DEFAULT_VIEW } = {}) {
  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(background);

  const camera = new THREE.PerspectiveCamera(58, 4 / 3, 0.05, 30);
  camera.position.set(...view.position);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.set(...view.target);
  controls.enableDamping = true;
  controls.maxDistance = 12;
  controls.update();
  controls.saveState();
  renderer.domElement.addEventListener('dblclick', () => controls.reset());

  // A small box where the Kinect is, so it is clear which way everything faces
  const kinect = new THREE.Mesh(new THREE.BoxGeometry(0.2, 0.05, 0.05), new THREE.MeshBasicMaterial({ color: 0x5d6673 }));
  scene.add(kinect);

  let container = null;
  let running = false;
  let resizeWatcher = null;
  let beforeRender = null;

  function fit() {
    const width = container.clientWidth;
    const height = container.clientHeight;
    if (width === 0 || height === 0) return;
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
  }

  // One loop per screen refresh; beforeRender applies the newest data, so at most one update per refresh
  function loop() {
    if (!running) return;
    if (beforeRender) beforeRender();
    controls.update();
    renderer.render(scene, camera);
    requestAnimationFrame(loop);
  }

  function show(element) {
    if (running && container === element) return;
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
    if (resizeWatcher) resizeWatcher.disconnect();
  }

  return {
    THREE, scene, camera, controls,
    show, hide,
    get running() { return running; },
    set beforeRender(fn) { beforeRender = fn; },
  };
}

export { THREE };
