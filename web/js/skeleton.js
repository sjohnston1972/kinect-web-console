// The Skeleton tab: tracked people drawn over the colour or depth picture, or in 3D,
// with tracking mode, smoothing, and a list of who is being tracked.
'use strict';

(() => {
  const $ = (id) => document.getElementById(id);
  const STALE_MS = 500;       // clear the drawing if skeletons stop arriving
  const NO_SIGNAL_MS = 2000;

  let view = 'overlay';       // 'overlay' or '3d'
  let picture = 'colour';     // what the overlay sits on: 'colour' or 'depth'
  let latest = null;          // newest skeletons message
  let latestAt = 0;
  let skeletonCount = 0;

  const overlay = $('skeleton-overlay');
  const overlayContext = overlay.getContext('2d');
  const colourDrawnAt = FrameView.attach($('skeleton-colour-canvas'), 1, 'colour');
  const depthDrawnAt = FrameView.attach($('skeleton-depth-canvas'), 2, 'depth');

  function streams() {
    if (view === '3d') return ['skeletons'];
    return [picture, 'skeletons'];
  }

  function applyChoices() {
    $('skeleton-view').dataset.view = view;
    $('skeleton-view').dataset.picture = picture;
    for (const input of document.querySelectorAll('input[name="skeleton-view"]')) input.checked = input.value === view;
    for (const input of document.querySelectorAll('input[name="skeleton-picture"]')) {
      input.checked = input.value === picture;
      input.disabled = view === '3d';
    }
    $('skeleton-picture-group').title = view === '3d' ? 'The 3D view shows the skeleton on its own, without a picture' : '';
    Tabs.setStreams('skeleton', streams());

    const show3d = view === '3d' && Tabs.current === 'skeleton';
    if (window.Skeleton3D) show3d ? Skeleton3D.show($('skeleton-3d')) : Skeleton3D.hide();
    try { localStorage.setItem('skeleton.view', JSON.stringify({ view, picture })); } catch { /* storage blocked */ }
  }

  // ----- Drawing over the picture -----

  function drawOverlay() {
    overlayContext.clearRect(0, 0, overlay.width, overlay.height);
    if (!latest || performance.now() - latestAt > STALE_MS) return;

    for (const body of latest.bodies) {
      const colour = SkeletonModel.colourFor(SkeletonModel.personOf(body));
      const at = (name) => body.joints[name] && body.joints[name][picture];

      for (const [a, b] of SkeletonModel.BONES) {
        const ja = body.joints[a];
        const jb = body.joints[b];
        if (!ja || !jb) continue;
        const guessed = ja.state === 'inferred' || jb.state === 'inferred';
        line(at(a), at(b), colour, guessed);
      }
      for (const [name, joint] of Object.entries(body.joints)) {
        dot(joint[picture], colour, joint.state === 'inferred', name === 'head' ? 9 : 5);
      }
    }
  }

  // Joints the Kinect is only guessing are drawn faded and dashed
  function line(p, q, colour, guessed) {
    const c = overlayContext;
    c.save();
    c.globalAlpha = guessed ? 0.4 : 1;
    c.setLineDash(guessed ? [6, 6] : []);
    c.lineCap = 'round';
    c.strokeStyle = 'rgba(0, 0, 0, 0.55)';   // dark edge so the line shows on any background
    c.lineWidth = 8;
    c.beginPath(); c.moveTo(p[0], p[1]); c.lineTo(q[0], q[1]); c.stroke();
    c.strokeStyle = colour;
    c.lineWidth = 4;
    c.beginPath(); c.moveTo(p[0], p[1]); c.lineTo(q[0], q[1]); c.stroke();
    c.restore();
  }

  function dot(p, colour, guessed, radius) {
    const c = overlayContext;
    c.save();
    c.globalAlpha = guessed ? 0.45 : 1;
    c.beginPath();
    c.arc(p[0], p[1], radius, 0, Math.PI * 2);
    c.fillStyle = guessed ? 'rgba(0, 0, 0, 0.4)' : colour;
    c.fill();
    c.lineWidth = 2;
    c.strokeStyle = guessed ? colour : 'rgba(0, 0, 0, 0.6)';
    c.stroke();
    c.restore();
  }

  // ----- People list -----

  function updatePeople() {
    const list = $('skeleton-people');
    const bodies = latest && performance.now() - latestAt <= STALE_MS ? latest.bodies : [];
    list.innerHTML = '';
    if (bodies.length === 0) {
      const p = document.createElement('p');
      p.className = 'muted small';
      p.textContent = $('mode-seated').checked
        ? 'Nobody tracked yet. Sit facing the Kinect, 1 to 2.5 m away, with your head and shoulders in view.'
        : 'Nobody tracked yet. Stand 1.5 to 3.5 m from the Kinect, facing it, with your whole body in view.';
      list.append(p);
      return;
    }
    for (const body of bodies) {
      const joints = Object.values(body.joints);
      const guessed = joints.filter((j) => j.state === 'inferred').length;
      const row = document.createElement('div');
      row.className = 'person';
      const swatch = document.createElement('span');
      swatch.className = 'swatch';
      swatch.style.background = SkeletonModel.colourFor(SkeletonModel.personOf(body));
      const text = document.createElement('span');
      text.textContent = `Person ${SkeletonModel.personOf(body)}: ${joints.length} joints${guessed ? `, ${guessed} guessed` : ''}`;
      row.append(swatch, text);
      list.append(row);
    }
  }

  // ----- Rates, once a second -----

  function updateRates() {
    if (Tabs.current !== 'skeleton') return;   // the tab on screen owns the shared counters
    const counts = LiveStats.take();
    $('skeleton-fps').textContent = `${skeletonCount} fps`;
    skeletonCount = 0;
    $('skeleton-picture-fps').textContent = view === '3d' ? 'off' : `${counts[picture] || 0} fps`;
    const delay = latest ? Date.now() - latest.timestamp : null;
    $('skeleton-delay').textContent = delay === null ? '--' : `${Math.max(0, delay)} ms`;

    const drawnAt = picture === 'colour' ? colourDrawnAt() : depthDrawnAt();
    $('skeleton-frame').classList.toggle('stale', view !== '3d' && performance.now() - drawnAt > NO_SIGNAL_MS);
    updatePeople();
  }

  // ----- Messages -----

  Connection.on('skeletons', (msg) => {
    latest = msg;
    latestAt = performance.now();
    skeletonCount++;
    if (view === 'overlay') requestAnimationFrame(drawOverlay);
    if (window.Skeleton3D) Skeleton3D.update(msg);
  });

  // Keep the tracking and smoothing buttons in step with the bridge (another tab may change them)
  Connection.on('status', (msg) => {
    if (!msg.skeleton) return;
    for (const input of document.querySelectorAll('input[name="skeleton-mode"]')) input.checked = input.value === msg.skeleton.mode;
    for (const input of document.querySelectorAll('input[name="skeleton-smoothing"]')) input.checked = input.value === msg.skeleton.smoothing;
  });

  for (const input of document.querySelectorAll('input[name="skeleton-view"]')) {
    input.addEventListener('change', () => { view = input.value; applyChoices(); });
  }
  for (const input of document.querySelectorAll('input[name="skeleton-picture"]')) {
    input.addEventListener('change', () => { picture = input.value; applyChoices(); });
  }
  for (const input of document.querySelectorAll('input[name="skeleton-mode"]')) {
    input.addEventListener('change', () => Connection.send({ type: 'skeleton.settings', mode: input.value }));
  }
  for (const input of document.querySelectorAll('input[name="skeleton-smoothing"]')) {
    input.addEventListener('change', () => Connection.send({ type: 'skeleton.settings', smoothing: input.value }));
  }

  Tabs.onShow((name) => {
    if (!window.Skeleton3D) return;
    if (name === 'skeleton' && view === '3d') Skeleton3D.show($('skeleton-3d'));
    else Skeleton3D.hide();
  });
  // skeleton3d.js loads a moment after this file; it calls this when ready
  window.onSkeleton3DReady = () => applyChoices();

  try {
    const saved = JSON.parse(localStorage.getItem('skeleton.view') || '{}');
    if (saved.view === 'overlay' || saved.view === '3d') view = saved.view;
    if (saved.picture === 'colour' || saved.picture === 'depth') picture = saved.picture;
  } catch { /* storage blocked or unreadable: use the defaults */ }
  applyChoices();
  setInterval(updateRates, 1000);
  setInterval(() => { if (view === 'overlay' && latest && performance.now() - latestAt > STALE_MS) drawOverlay(); }, 250);
})();
