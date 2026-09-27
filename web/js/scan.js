// The 3D scan tab: Kinect Fusion's live shaded model, presets, start, pause, reset, colour capture,
// a tracking-lost warning, export to STL, OBJ or PLY, and the finished model in 3D.
'use strict';

(() => {
  const $ = (id) => document.getElementById(id);

  let status = null;            // newest fusion message
  let sensorReady = false;
  let view = 'live';            // 'live' (the shaded preview) or 'model' (the finished mesh)
  let modelLoaded = false;

  const previewDrawnAt = FrameView.attach($('scan-canvas'), 4, 'fusion');

  function setView(name) {
    view = name;
    $('scan-view').dataset.view = name;
    for (const input of document.querySelectorAll('input[name="scan-view"]')) input.checked = input.value === name;
    const showModel = name === 'model' && Tabs.current === 'scan';
    if (window.ScanMesh) showModel ? ScanMesh.show($('scan-3d')) : ScanMesh.hide();
  }

  function presetText(p) {
    return `${p.size[0]} × ${p.size[1]} × ${p.size[2]} m, ${p.detailMm} mm detail, starts ${p.startDistance} m from the Kinect`;
  }

  function render() {
    if (!status) return;
    const s = status;
    const scanning = s.state === 'scanning';
    const hasScan = s.framesIntegrated > 0;

    for (const input of document.querySelectorAll('input[name="scan-preset"]')) input.checked = input.value === s.preset;
    const preset = s.presets.find((p) => p.name === s.preset);
    $('scan-preset-text').textContent = preset ? presetText(preset) : '';
    if (document.activeElement !== $('scan-colour')) $('scan-colour').checked = s.colour;

    const start = $('scan-start');
    start.textContent = scanning ? 'Pause' : s.state === 'paused' ? 'Carry on' : 'Start';
    start.disabled = !scanning && !sensorReady;
    start.title = !scanning && !sensorReady ? 'The Kinect is not ready. The status light says why.' : '';
    $('scan-reset').disabled = !hasScan && !scanning;

    for (const id of ['scan-stl', 'scan-obj', 'scan-ply', 'scan-model']) {
      $(id).disabled = !hasScan;
      $(id).title = hasScan ? '' : 'Scan something first: press Start and move the Kinect slowly around it';
    }

    // The big warning over the preview when Fusion cannot line up the depth picture with the model
    const lost = scanning && s.tracking === 'lost';
    $('scan-lost').hidden = !lost;

    let line;
    if (!sensorReady) line = 'The Kinect is not ready.';
    else if (lost) line = 'Tracking lost.';
    else if (scanning) line = `Scanning: ${s.framesIntegrated} frames merged, ${s.fps} per second.`;
    else if (s.state === 'paused') line = `Paused with ${s.framesIntegrated} frames merged. Export, carry on, or reset.`;
    else line = 'Ready. Press Start, then move the Kinect slowly around what you are scanning.';
    $('scan-state').textContent = line;
    $('scan-state').className = lost ? 'small warn-text' : 'muted small';

    $('scan-processor').textContent = s.processor || 'Starts when you press Start';
    $('scan-warning').hidden = !(s.processorWarning || s.error);
    $('scan-warning').textContent = s.error || s.processorWarning || '';

    $('scan-hint').hidden = scanning || hasScan;
    $('scan-canvas-frame').classList.toggle('stale', performance.now() - previewDrawnAt() > 2000);

    const list = $('scan-files');
    list.innerHTML = '';
    for (const f of s.files) {
      const link = document.createElement('a');
      link.href = f.url;
      link.download = f.name;
      link.textContent = `${f.name} (${f.sizeMb} MB)`;
      list.append(link);
    }
    if (s.files.length === 0) {
      const p = document.createElement('p');
      p.className = 'muted small';
      p.textContent = 'Nothing exported yet.';
      list.append(p);
    }
  }

  function exportAs(format) {
    $('scan-note').textContent = format === 'preview' ? 'Building the 3D model…' : `Saving ${format.toUpperCase()}… (big scans take a few seconds)`;
    Connection.send({ type: 'export', kind: 'scan', format });
  }

  Connection.on('fusion', (msg) => { status = msg; render(); });

  Connection.on('export', async (msg) => {
    if (msg.kind !== 'scan') return;
    if (msg.format === 'preview') {
      if (!window.ScanMesh) return;
      try {
        await ScanMesh.load(msg.url);
        modelLoaded = true;
        setView('model');
        $('scan-note').textContent = `3D model: ${msg.note}`;
      } catch (err) {
        $('scan-note').textContent = '';
        Notice.show(`Could not show the 3D model: ${err.message}`, 'error');
      }
      return;
    }
    $('scan-note').textContent = `Saved ${msg.name}: ${msg.note}`;
    const link = document.createElement('a');
    link.href = msg.url;
    link.download = msg.name;
    document.body.append(link);
    link.click();
    link.remove();
  });

  Connection.on('error', (msg) => {
    if (msg.code === 'exportFailed') $('scan-note').textContent = '';
  });

  Connection.on('status', (msg) => { sensorReady = msg.sensor.state === 'ready'; render(); });
  Connection.on('close', () => { sensorReady = false; render(); });

  // ----- Controls -----

  $('scan-start').addEventListener('click', () => {
    if (status && status.state === 'scanning') Connection.send({ type: 'fusion.pause' });
    else {
      setView('live');
      Connection.send({ type: 'fusion.start' });
    }
  });
  $('scan-reset').addEventListener('click', () => {
    if (status && status.framesIntegrated > 0 && !confirm('Reset clears the scan so far. Export first if you want to keep it.\n\nClear the scan?')) return;
    Connection.send({ type: 'fusion.reset' });
  });
  $('scan-lost-reset').addEventListener('click', () => Connection.send({ type: 'fusion.reset' }));

  for (const input of document.querySelectorAll('input[name="scan-preset"]')) {
    input.addEventListener('change', () => {
      if (status && status.framesIntegrated > 0 && !confirm('Changing the preset clears the scan so far. Export first if you want to keep it.\n\nChange preset?')) {
        render();
        return;
      }
      Connection.send({ type: 'fusion.preset', preset: input.value });
    });
  }
  $('scan-colour').addEventListener('change', () => Connection.send({ type: 'fusion.colour', on: $('scan-colour').checked }));

  $('scan-stl').addEventListener('click', () => exportAs('stl'));
  $('scan-obj').addEventListener('click', () => exportAs('obj'));
  $('scan-ply').addEventListener('click', () => exportAs('ply'));
  $('scan-model').addEventListener('click', () => exportAs('preview'));

  for (const input of document.querySelectorAll('input[name="scan-view"]')) {
    input.addEventListener('change', () => {
      if (input.value === 'model' && !modelLoaded) {
        exportAs('preview');
        render();
        return;
      }
      setView(input.value);
    });
  }

  Tabs.setStreams('scan', ['fusion']);
  Tabs.onShow((name) => {
    if (!window.ScanMesh) return;
    if (name === 'scan' && view === 'model') ScanMesh.show($('scan-3d'));
    else ScanMesh.hide();
  });
  window.onScanMeshReady = () => setView(view);

  setView('live');
  setInterval(render, 1000);
})();
