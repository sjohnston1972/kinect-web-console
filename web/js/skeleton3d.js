// The 3D skeleton: each tracked person as coloured joints and bones in their real position, in metres,
// standing on a grid laid along the floor the Kinect detects. Drag to orbit, as with the point cloud.
// createSkeletonScene makes one such view; the Skeleton tab and the Motion capture tab each have their own.

import { createView, THREE } from './scene3d.js';

const JOINT_RADIUS = 0.035;
const HEAD_RADIUS = 0.08;
const BONE_RADIUS = 0.018;
const GUESSED_OPACITY = 0.3;
const MAX_PLAYERS = 6;

const up = new THREE.Vector3(0, 1, 0);
const a = new THREE.Vector3();
const b = new THREE.Vector3();

// Kinect skeleton space has Z pointing away from the sensor; the 3D view looks down -Z
function toScene(p, out) {
  return out.set(p[0], p[1], -p[2]);
}

function makeFigure(player) {
  const colour = new THREE.Color(SkeletonModel.colourFor(player));
  const solid = new THREE.MeshBasicMaterial({ color: colour });
  const faded = new THREE.MeshBasicMaterial({ color: colour, transparent: true, opacity: GUESSED_OPACITY });
  const group = new THREE.Group();

  const joints = {};
  const jointGeometry = new THREE.SphereGeometry(1, 12, 8);
  const bones = SkeletonModel.BONES.map(() => {
    const mesh = new THREE.Mesh(new THREE.CylinderGeometry(BONE_RADIUS, BONE_RADIUS, 1, 8), solid);
    group.add(mesh);
    return mesh;
  });

  function joint(name) {
    if (!joints[name]) {
      const mesh = new THREE.Mesh(jointGeometry, solid);
      mesh.scale.setScalar(name === 'head' ? HEAD_RADIUS : JOINT_RADIUS);
      group.add(mesh);
      joints[name] = mesh;
    }
    return joints[name];
  }

  function update(body) {
    group.visible = true;
    for (const mesh of Object.values(joints)) mesh.visible = false;
    for (const [name, j] of Object.entries(body.joints)) {
      const mesh = joint(name);
      toScene(j.p, mesh.position);
      mesh.material = j.state === 'inferred' ? faded : solid;
      mesh.visible = true;
    }
    SkeletonModel.BONES.forEach(([from, to], i) => {
      const mesh = bones[i];
      const ja = body.joints[from];
      const jb = body.joints[to];
      if (!ja || !jb) { mesh.visible = false; return; }
      toScene(ja.p, a);
      toScene(jb.p, b);
      // A unit-length cylinder, stretched to the bone's length and turned to point along it
      mesh.position.copy(a).add(b).multiplyScalar(0.5);
      const length = a.distanceTo(b);
      mesh.scale.set(1, Math.max(length, 0.001), 1);
      mesh.quaternion.setFromUnitVectors(up, b.sub(a).normalize());
      mesh.material = ja.state === 'inferred' || jb.state === 'inferred' ? faded : solid;
      mesh.visible = true;
    });
  }

  return { group, update };
}

// The floor plane from the Kinect is Ax + By + Cz + D = 0 in its own coordinates, which tilt with the sensor
// (in the 3D view, with Z flipped, that becomes Ax + By - Cz + D = 0)
const ROOM_CENTRE = new THREE.Vector3(0, 0, -2.5);   // centre the grid out in the room, not under the Kinect

function placeFloor(floorGrid, floor) {
  if (!floor || floor[1] === 0) { floorGrid.visible = false; return; }
  const length = Math.hypot(floor[0], floor[1], floor[2]);
  const normal = new THREE.Vector3(floor[0], floor[1], -floor[2]).divideScalar(length);
  const offset = floor[3] / length;
  // Drop the room centre straight onto the floor plane
  const distance = normal.dot(ROOM_CENTRE) + offset;
  floorGrid.position.copy(ROOM_CENTRE).addScaledVector(normal, -distance);
  floorGrid.quaternion.setFromUnitVectors(up, normal);
  floorGrid.visible = true;
}

export function createSkeletonScene() {
  let view = null;
  let waiting = null;   // newest message not yet drawn
  let floorGrid = null;
  const figures = [];   // one reusable set of meshes per player number

  function build() {
    view = createView({ view: { position: [1.8, 1.2, 0.2], target: [0, -0.2, -2.3] } });
    floorGrid = new THREE.GridHelper(6, 12, 0x3a4250, 0x262c35);
    floorGrid.visible = false;
    view.scene.add(floorGrid);

    for (let player = 1; player <= MAX_PLAYERS; player++) {
      const figure = makeFigure(player);
      figure.group.visible = false;
      view.scene.add(figure.group);
      figures[player] = figure;
    }

    view.beforeRender = () => {
      if (!waiting) return;
      const msg = waiting;
      waiting = null;
      placeFloor(floorGrid, msg.floor);
      const seen = new Set(msg.bodies.map((body) => body.player));
      for (let player = 1; player <= MAX_PLAYERS; player++) {
        if (!seen.has(player)) figures[player].group.visible = false;
      }
      for (const body of msg.bodies) figures[body.player]?.update(body);
    };
  }

  function show(element) {
    if (!view) build();
    view.show(element);
  }

  function hide() {
    waiting = null;
    if (view) view.hide();
  }

  // Takes a skeletons message (or anything shaped like one: { bodies, floor })
  function update(msg) {
    if (view && view.running) waiting = msg;
  }

  return { show, hide, update };
}

window.createSkeletonScene = createSkeletonScene;
window.Skeleton3D = createSkeletonScene();
if (window.onMocapViewReady) window.onMocapViewReady();
if (window.onSkeleton3DReady) window.onSkeleton3DReady();
