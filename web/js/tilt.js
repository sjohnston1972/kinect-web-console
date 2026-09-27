// The tilt slider in the header. Drag it and let go to move the Kinect.
// The bridge enforces the motor's limits (one move a second, 15 in 20 seconds) and says if it refuses.
'use strict';

(() => {
  const slider = document.getElementById('tilt-slider');
  const readout = document.getElementById('tilt-value');
  const holder = document.getElementById('tilt');

  let dragging = false;
  let actual = null;   // the angle the Kinect reports

  function degrees(value) {
    return value == null ? '--' : `${value > 0 ? '+' : ''}${value}°`;
  }

  function setEnabled(enabled, why) {
    slider.disabled = !enabled;
    holder.title = enabled ? 'Drag and let go to tilt the Kinect. Limited to one move a second to protect the motor.' : why;
  }

  Connection.on('status', (msg) => {
    actual = msg.tilt ? msg.tilt.angle : null;
    const moving = msg.tilt && msg.tilt.moving;

    if (msg.sensor.state !== 'ready') setEnabled(false, 'The Kinect is not ready, so it cannot tilt.');
    else if (moving) setEnabled(false, 'The Kinect is tilting. Wait for it to stop.');
    else setEnabled(true);

    if (!dragging) {
      if (actual != null) slider.value = actual;
      readout.textContent = moving ? 'moving' : degrees(actual);
    }
  });

  Connection.on('close', () => {
    actual = null;
    readout.textContent = '--';
    setEnabled(false, 'Waiting for the bridge');
  });

  // Put the slider back where the Kinect really is when a move is refused
  Connection.on('error', (msg) => {
    if (msg.code === 'tiltRefused' && actual != null) {
      slider.value = actual;
      readout.textContent = degrees(actual);
    }
  });

  slider.addEventListener('pointerdown', () => { dragging = true; });
  slider.addEventListener('pointerup', () => { dragging = false; });
  slider.addEventListener('input', () => { readout.textContent = degrees(Number(slider.value)); });
  slider.addEventListener('change', () => {
    dragging = false;
    const angle = Number(slider.value);
    if (angle === actual) return;
    readout.textContent = 'moving';
    Connection.send({ type: 'tilt', angle });
  });
  // Keyboard arrows fire change on every press; the bridge's rate limit covers that
  slider.addEventListener('blur', () => { dragging = false; });

  setEnabled(false, 'Waiting for the bridge');
})();
