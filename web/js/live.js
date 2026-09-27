// The Live tab: colour and depth pictures, the depth legend, the 3D point cloud switch,
// people highlight, snapshots, and frame rates.
'use strict';

// Frame rate and delay counters, shared with the point cloud (pointcloud.js)
const LiveStats = (() => {
  const counts = {};
  const delays = {};
  function tick(stream, timestamp) {
    counts[stream] = (counts[stream] || 0) + 1;
    delays[stream] = Date.now() - timestamp;
  }
  // Returns frames counted since the last call, and clears the counts
  function take() {
    const result = { ...counts };
    for (const key of Object.keys(counts)) counts[key] = 0;
    return result;
  }
  return { tick, take, delays };
})();

(() => {
  const $ = (id) => document.getElementById(id);
  const VIEWS = {
    both: ['colour', 'depth'],
    colour: ['colour'],
    depth: ['depth'],
    cloud: ['depthRaw'],
  };
  const NO_SIGNAL_MS = 2000;
  const LEGEND_TICKS_M = [0.8, 1.5, 2, 2.5, 3, 3.5, 4];

  let view = 'both';
  let sensorReady = false;
  const lastFrameAt = { colour: 0, depth: 0 };

  // Draws JPEG frames onto a canvas. Decoding happens off the main thread (createImageBitmap).
  // If a frame arrives while the last is still decoding, only the newest waiting one is kept.
  function makeRenderer(canvas, stream) {
    const context = canvas.getContext('2d');
    let busy = false;
    let waiting = null;

    async function draw(frame) {
      busy = true;
      try {
        const bitmap = await createImageBitmap(new Blob([frame.payload], { type: 'image/jpeg' }));
        context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        bitmap.close();
        lastFrameAt[stream] = performance.now();
        LiveStats.tick(stream, frame.timestamp);
      } catch (err) {
        console.warn(`Could not show a ${stream} frame`, err);
      }
      busy = false;
      if (waiting) {
        const next = waiting;
        waiting = null;
        draw(next);
      }
    }

    return (frame) => {
      if (busy) waiting = frame;
      else draw(frame);
    };
  }

  Connection.on('stream:1', makeRenderer($('colour-canvas'), 'colour'));
  Connection.on('stream:2', makeRenderer($('depth-canvas'), 'depth'));

  function buildLegend() {
    $('legend-bar').style.background = DepthColours.cssGradient();
    const span = DepthColours.MAX_MM - DepthColours.MIN_MM;
    for (const metres of LEGEND_TICKS_M) {
      const tick = document.createElement('span');
      tick.style.left = `${((metres * 1000 - DepthColours.MIN_MM) / span) * 100}%`;
      tick.textContent = `${metres} m`;
      $('legend-ticks').append(tick);
    }
  }

  function setView(name) {
    if (!(name in VIEWS)) name = 'both';
    view = name;
    $('live-view').dataset.view = name;
    for (const input of document.querySelectorAll('input[name="live-view"]')) input.checked = input.value === name;
    Tabs.setStreams('live', VIEWS[name]);
    try { localStorage.setItem('live.view', name); } catch { /* storage blocked: just forget the choice */ }

    const cloudShown = name === 'cloud' && Tabs.current === 'live';
    if (window.PointCloud) cloudShown ? PointCloud.show($('cloud')) : PointCloud.hide();

    const highlightUseful = name === 'both' || name === 'depth';
    $('highlight').disabled = !highlightUseful;
    $('highlight-row').title = highlightUseful ? 'Show tracked people in full colour and everything else in grey'
      : 'People highlight applies to the depth picture. Switch to a view that shows depth.';
  }

  function updateSnapshotButton() {
    const button = $('snapshot-btn');
    button.disabled = !sensorReady;
    button.title = sensorReady ? 'Save the current colour and depth pictures as PNG files'
      : 'The Kinect is not sending pictures right now. The status light says why.';
  }

  function showSnapshot(msg) {
    const list = $('snapshot-files');
    list.innerHTML = '';
    for (const file of msg.files) {
      const link = document.createElement('a');
      link.href = file.url;
      link.textContent = file.name;
      link.download = file.name;
      list.append(link);
    }
    Notice.show('Snapshot saved to captures\\snapshots', 'info');
  }

  // Frame rates and delay, once a second. Also shows "waiting" over a picture that has stopped updating.
  function updateRates() {
    const counts = LiveStats.take();
    const text = (stream) => (VIEWS[view].includes(stream) ? `${counts[stream] || 0} fps` : 'off');
    $('fps-colour').textContent = text('colour');
    $('fps-depth').textContent = text('depth');
    $('fps-cloud').textContent = text('depthRaw');

    const delays = VIEWS[view].map((s) => LiveStats.delays[s]).filter((d) => d !== undefined);
    $('delay').textContent = delays.length ? `${Math.max(0, Math.round(Math.max(...delays)))} ms` : '--';

    const now = performance.now();
    $('colour-figure').classList.toggle('stale', now - lastFrameAt.colour > NO_SIGNAL_MS);
    $('depth-figure').classList.toggle('stale', now - lastFrameAt.depth > NO_SIGNAL_MS);
  }

  Connection.on('status', (msg) => {
    sensorReady = msg.sensor.state === 'ready';
    updateSnapshotButton();
    if (msg.live && document.activeElement !== $('highlight')) $('highlight').checked = msg.live.peopleHighlight;
  });
  Connection.on('close', () => { sensorReady = false; updateSnapshotButton(); });
  Connection.on('snapshot', showSnapshot);

  for (const input of document.querySelectorAll('input[name="live-view"]')) {
    input.addEventListener('change', () => setView(input.value));
  }
  $('highlight').addEventListener('change', () =>
    Connection.send({ type: 'live.settings', peopleHighlight: $('highlight').checked }));
  $('snapshot-btn').addEventListener('click', () => Connection.send({ type: 'snapshot' }));

  // The point cloud only draws while its tab and view are showing
  Tabs.onShow((name) => {
    if (!window.PointCloud) return;
    if (name === 'live' && view === 'cloud') PointCloud.show($('cloud'));
    else PointCloud.hide();
  });
  // pointcloud.js loads a moment after this file; it calls this when ready
  window.onPointCloudReady = () => setView(view);

  buildLegend();
  let saved = 'both';
  try { saved = localStorage.getItem('live.view') || 'both'; } catch { /* storage blocked */ }
  setView(saved);
  updateSnapshotButton();
  setInterval(updateRates, 1000);
})();
