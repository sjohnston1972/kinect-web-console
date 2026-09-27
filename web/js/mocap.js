// The Motion capture tab: record takes (with a countdown), a list of saved takes,
// playback with a scrub bar, rename, delete, and export as BVH or JSON.
// The 3D view shows the live skeleton, or the take being played.
'use strict';

(() => {
  const $ = (id) => document.getElementById(id);

  let scene = null;             // this tab's own 3D skeleton view (from skeleton3d.js)
  let sensorReady = false;
  let state = 'idle';           // from the bridge: idle, countdown, recording, saving
  let peopleNow = 0;
  let takes = [];

  // Playback
  let take = null;              // the loaded take file
  let takeId = null;
  let playing = false;
  let playTime = 0;             // seconds into the take
  let lastTick = 0;
  let loading = false;

  // ----- Recording -----

  function updateRecordButton() {
    const button = $('mocap-record');
    const busy = state === 'countdown' || state === 'recording';
    button.classList.toggle('stop', busy);
    button.textContent = busy ? '■ Stop' : state === 'saving' ? 'Saving…' : '● Record';
    const canStart = sensorReady && state === 'idle' && Connection.isOpen();
    button.disabled = !(busy || canStart);
    button.title = busy ? (state === 'countdown' ? 'Cancel the countdown' : 'Stop and save the take')
      : !Connection.isOpen() ? 'Waiting for the bridge'
      : !sensorReady ? 'The Kinect is not ready. The status light says why.'
      : state === 'saving' ? 'Saving the last take'
      : 'Start a 3-second countdown, then record';

    let text;
    if (state === 'countdown') text = 'Get into position…';
    else if (state === 'recording') text = peopleNow > 0 ? `Recording: ${peopleNow} ${peopleNow === 1 ? 'person' : 'people'} tracked.` : 'Recording, but nobody is tracked. Step into view.';
    else if (state === 'saving') text = 'Saving the take…';
    else if (!sensorReady) text = 'Recording needs the Kinect. The status light says what to check.';
    else if (peopleNow === 0) text = 'Nobody is tracked right now. Press Record, then step into view during the countdown.';
    else text = `${peopleNow} ${peopleNow === 1 ? 'person' : 'people'} tracked. Ready to record.`;
    $('mocap-state').textContent = text;
  }

  function updateHud(msg) {
    const hud = $('mocap-hud');
    hud.className = 'hud';
    if (msg && msg.state === 'countdown') {
      hud.classList.add('countdown');
      hud.textContent = msg.countdown;
    } else if (msg && msg.state === 'recording') {
      hud.classList.add('recording');
      hud.textContent = `● REC  ${clock(msg.elapsed)}`;
    } else if (take) {
      hud.classList.add('playing');
      hud.textContent = `▶ ${take.name}`;
    } else {
      hud.textContent = '';
    }
  }

  $('mocap-record').addEventListener('click', () => {
    if (state === 'countdown' || state === 'recording') Connection.send({ type: 'mocap.stop' });
    else {
      stopPlayback();   // go back to the live view so the countdown and recording are visible
      Connection.send({ type: 'mocap.start' });
    }
  });

  // ----- Take list -----

  function renderTakes() {
    const list = $('take-list');
    list.innerHTML = '';
    if (takes.length === 0) {
      const p = document.createElement('p');
      p.className = 'muted small';
      p.textContent = 'No takes yet. Record one above.';
      list.append(p);
      return;
    }
    for (const t of takes) {
      const row = document.createElement('div');
      row.className = 'take' + (t.id === takeId ? ' selected' : '');

      const open = document.createElement('button');
      open.className = 'take-open';
      open.title = 'Play this take';
      const name = document.createElement('span');
      name.className = 'take-name';
      name.textContent = t.name;
      const meta = document.createElement('span');
      meta.className = 'take-meta';
      meta.textContent = `${clock(t.duration)} · ${t.people} ${t.people === 1 ? 'person' : 'people'} · ${t.mode} · ${t.created.slice(5, 16)}`;
      open.append(name, meta);
      open.addEventListener('click', () => loadTake(t.id));

      const rename = smallButton('Rename', 'Give this take a new name', () => {
        const newName = prompt('New name for this take:', t.name);
        if (newName && newName.trim() && newName.trim() !== t.name) Connection.send({ type: 'mocap.rename', id: t.id, name: newName.trim() });
      });
      const remove = smallButton('Delete', 'Delete this take (it goes to the Recycle Bin)', () => {
        if (confirm(`Delete "${t.name}"?\n\nIt goes to the Recycle Bin, so you can get it back from there.`)) Connection.send({ type: 'mocap.delete', id: t.id });
      });

      const actions = document.createElement('div');
      actions.className = 'take-actions';
      actions.append(rename, remove);
      row.append(open, actions);
      list.append(row);
    }
  }

  function smallButton(text, title, onClick) {
    const b = document.createElement('button');
    b.className = 'button small';
    b.textContent = text;
    b.title = title;
    b.addEventListener('click', onClick);
    return b;
  }

  // ----- Playback -----

  async function loadTake(id) {
    if (loading || state === 'countdown' || state === 'recording') return;
    loading = true;
    try {
      const response = await fetch(`/captures/mocap/${encodeURIComponent(id)}.json`, { cache: 'no-store' });
      if (!response.ok) throw new Error(`the bridge answered ${response.status}`);
      take = await response.json();
      takeId = id;
      playTime = 0;
      playing = true;
      lastTick = performance.now();
      $('player').hidden = false;
      $('player-name').textContent = take.name;
      $('export-note').textContent = '';
      $('scrub').max = Math.max(1, Math.round(take.durationSeconds * 1000));
      renderTakes();
      updateHud(null);
      requestAnimationFrame(playbackLoop);
    } catch (err) {
      Notice.show(`Could not open that take: ${err.message}`, 'error');
    } finally {
      loading = false;
    }
  }

  // The frame at a moment in the take: the last one at or before it (frames are in time order)
  function frameAt(seconds) {
    const frames = take.frames;
    const ms = seconds * 1000;
    let lo = 0, hi = frames.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (frames[mid].t <= ms) lo = mid; else hi = mid - 1;
    }
    return frames[lo];
  }

  function showFrame() {
    if (!take || take.frames.length === 0) return;
    const frame = frameAt(playTime);
    if (scene) scene.update({ bodies: frame.bodies, floor: take.floor });
    $('scrub').value = Math.round(playTime * 1000);
    $('player-time').textContent = `${clock(playTime)} / ${clock(take.durationSeconds)}`;
    $('play-pause').textContent = playing ? 'Pause' : playTime >= take.durationSeconds ? 'Play again' : 'Play';
  }

  function playbackLoop(now) {
    if (!take) return;
    if (playing) {
      playTime += (now - lastTick) / 1000;
      if (playTime >= take.durationSeconds) {
        playTime = take.durationSeconds;
        playing = false;
      }
    }
    lastTick = now;
    showFrame();
    if (playing) requestAnimationFrame(playbackLoop);
  }

  function setPlaying(on) {
    if (!take) return;
    if (on && playTime >= take.durationSeconds) playTime = 0;
    playing = on;
    lastTick = performance.now();
    showFrame();
    if (on) requestAnimationFrame(playbackLoop);
  }

  function stopPlayback() {
    take = null;
    takeId = null;
    playing = false;
    $('player').hidden = true;
    renderTakes();
    updateHud(null);
  }

  $('play-pause').addEventListener('click', () => setPlaying(!playing));
  $('scrub').addEventListener('input', () => {
    playing = false;
    playTime = Number($('scrub').value) / 1000;
    showFrame();
  });
  $('back-live').addEventListener('click', stopPlayback);
  $('export-bvh').addEventListener('click', () => {
    $('export-note').textContent = 'Making the BVH file…';
    Connection.send({ type: 'export', kind: 'take', id: takeId, format: 'bvh' });
  });
  $('export-json').addEventListener('click', () => Connection.send({ type: 'export', kind: 'take', id: takeId, format: 'json' }));

  // ----- Messages -----

  // Hands-free: the gesture presses Record or Stop, and beeps follow the recording's progress
  HandsFree.register('mocap', {
    label: () => (state === 'countdown' || state === 'recording' ? 'stop recording' : 'start recording'),
    run: () => { if (!$('mocap-record').disabled) $('mocap-record').click(); else HandsFree.sounds.problem(); },
  });
  let lastCountdown = 0;

  function soundsFor(msg) {
    if (msg.state === 'countdown' && msg.countdown !== lastCountdown) HandsFree.sounds.countdown();
    lastCountdown = msg.state === 'countdown' ? msg.countdown : 0;
    if (msg.state === 'recording' && state === 'countdown') HandsFree.sounds.started();
    if (msg.state === 'saving' && state === 'recording') HandsFree.sounds.stopped();
    if (msg.event && msg.event.kind === 'saved') HandsFree.sounds.saved();
    if (msg.event && msg.event.kind === 'failed') HandsFree.sounds.problem();
  }

  Connection.on('mocap', (msg) => {
    soundsFor(msg);
    state = msg.state;
    peopleNow = msg.peopleNow;
    if (msg.takes) {
      takes = msg.takes;
      renderTakes();
    }
    if (msg.event) {
      const e = msg.event;
      if (e.kind === 'saved') Notice.show(`Saved: ${e.name}`, 'info');
      if (e.kind === 'failed') Notice.show(e.message, 'error');
      if (e.kind === 'renamed' && takeId === e.oldId) {
        takeId = e.id;
        take.name = e.name;
        $('player-name').textContent = e.name;
        renderTakes();
      }
      if (e.kind === 'deleted' && takeId === e.id) stopPlayback();
    }
    updateRecordButton();
    if (!take || msg.state === 'countdown' || msg.state === 'recording') updateHud(msg);
  });

  Connection.on('export', (msg) => {
    if (msg.kind !== 'take') return;
    if (msg.note) $('export-note').textContent = msg.note;
    else $('export-note').textContent = '';
    // Start the download: the bridge serves captures as attachments
    const link = document.createElement('a');
    link.href = msg.url;
    link.download = msg.name;
    document.body.append(link);
    link.click();
    link.remove();
  });

  Connection.on('error', (msg) => {
    if (msg.code === 'exportFailed') $('export-note').textContent = '';
  });

  // Live skeletons fill the view whenever no take is playing
  Connection.on('skeletons', (msg) => {
    if (!take && scene) scene.update(msg);
  });

  Connection.on('status', (msg) => {
    sensorReady = msg.sensor.state === 'ready';
    updateRecordButton();
  });
  Connection.on('close', () => { sensorReady = false; updateRecordButton(); });

  // ----- The 3D view -----

  Tabs.setStreams('mocap', ['skeletons']);
  Tabs.onShow((name) => {
    if (!scene) return;
    if (name === 'mocap') scene.show($('mocap-3d'));
    else scene.hide();
  });

  // skeleton3d.js loads a moment after this file; it calls this when ready
  window.onMocapViewReady = () => {
    scene = window.createSkeletonScene();
    if (Tabs.current === 'mocap') scene.show($('mocap-3d'));
    if (take) showFrame();
  };
  if (window.createSkeletonScene) window.onMocapViewReady();

  function clock(seconds) {
    const s = Math.max(0, Math.floor(seconds));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  }

  updateRecordButton();
  renderTakes();
})();
