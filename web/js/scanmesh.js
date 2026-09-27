// The finished 3D scan in an orbitable view: loads the bridge's preview PLY file and shows it lit,
// in colour if the scan captured colour. Drag to orbit, scroll to zoom, right-drag to pan, double-click to reset.

import { createView, THREE } from './scene3d.js';
import { PLYLoader } from '../vendor/three/PLYLoader.js';

let view = null;
let current = null;

function build() {
  view = createView({ background: 0x1b1f24, view: { position: [0, 1.2, 2.6], target: [0, 0.6, 0] } });
  view.scene.add(new THREE.HemisphereLight(0xffffff, 0x404850, 2.2));
  const sun = new THREE.DirectionalLight(0xffffff, 1.6);
  sun.position.set(2, 4, 3);
  view.scene.add(sun);
  view.scene.add(new THREE.GridHelper(4, 16, 0x3a4250, 0x2a3038));
}

function show(element) {
  if (!view) build();
  view.show(element);
}

function hide() {
  if (view) view.hide();
}

// The PLY is in metres with Z up; the 3D view has Y up, so stand it upright
async function load(url) {
  if (!view) build();
  const geometry = await new PLYLoader().loadAsync(url);
  geometry.computeVertexNormals();
  const hasColour = geometry.hasAttribute('color');
  const material = new THREE.MeshStandardMaterial({
    color: hasColour ? 0xffffff : 0xb8c0cc, vertexColors: hasColour, roughness: 0.85, metalness: 0, side: THREE.DoubleSide,
  });
  if (current) {
    view.scene.remove(current);
    current.geometry.dispose();
    current.material.dispose();
  }
  current = new THREE.Mesh(geometry, material);
  current.rotation.x = -Math.PI / 2;
  view.scene.add(current);

  // Aim the camera at the model, far enough back to see all of it
  const box = new THREE.Box3().setFromObject(current);
  const size = box.getSize(new THREE.Vector3());
  const centre = box.getCenter(new THREE.Vector3());
  const distance = Math.max(size.x, size.y, size.z) * 1.6 + 0.5;
  view.controls.target.copy(centre);
  view.camera.position.set(centre.x + distance * 0.35, centre.y + distance * 0.3, centre.z + distance);
  view.controls.update();
  view.controls.saveState();
  return { triangles: geometry.index ? geometry.index.count / 3 : 0, colour: hasColour };
}

window.ScanMesh = { show, hide, load };
if (window.onScanMeshReady) window.onScanMeshReady();
