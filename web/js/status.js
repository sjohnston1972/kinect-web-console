// The status light in the header, the mock badge, and everything on the Status tab.
'use strict';

(() => {
  const MAX_LOG_LINES = 50;
  const STREAM_NAMES = { colour: 'Colour', depth: 'Depth' };
  const $ = (id) => document.getElementById(id);

  const BRIDGE_DOWN = {
    light: 'red',
    title: 'The bridge is not running',
    help: 'Double-click run.cmd in the kinect folder to start it. This page reconnects by itself once it is running.',
  };

  function setLight(light, title, help) {
    $('status-light').dataset.light = light;
    $('fault-light').dataset.light = light;
    $('fault-card').dataset.light = light;
    $('status-text').textContent = title;
    $('status-pill').title = help || '';
    $('fault-title').textContent = title;
    $('fault-help').textContent = help || 'Everything is working.';
  }

  function degrees(value) {
    return value == null ? '--' : `${value > 0 ? '+' : ''}${value}°`;
  }

  function levelText(angle) {
    if (angle == null) return '--';
    return Math.abs(angle) <= 1.5 ? `${degrees(angle)}  (level)` : degrees(angle);
  }

  function uptimeText(seconds) {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = seconds % 60;
    return h > 0 ? `${h} h ${m} min` : m > 0 ? `${m} min ${s} s` : `${s} s`;
  }

  function showStatus(msg) {
    setLight(msg.sensor.light, msg.sensor.title, msg.sensor.help);
    $('mock-badge').hidden = !msg.mock;

    $('st-mode').textContent = msg.mock ? 'Mock (fake sensor)' : 'Real Kinect';
    $('st-state').textContent = msg.sensor.state;
    $('st-detail').textContent = msg.sensor.detail || '--';
    $('st-uptime').textContent = uptimeText(msg.uptimeSeconds);

    const tilt = msg.tilt ? msg.tilt.angle : null;
    $('st-tilt').textContent = degrees(tilt);

    const a = msg.accelerometer;
    $('st-side').textContent = a ? levelText(a.sideTilt) : '--';
    $('st-front').textContent = a ? degrees(a.frontTilt) : '--';
    $('st-accel').textContent = a ? `x ${a.x}, y ${a.y}, z ${a.z}` : '--';

    const rates = Object.entries(msg.fps || {});
    $('st-fps').innerHTML = '';
    if (rates.length === 0) rates.push(['Streams', 'None running yet']);
    for (const [name, value] of rates) {
      const dt = document.createElement('dt');
      const dd = document.createElement('dd');
      dt.textContent = STREAM_NAMES[name] || name;
      dd.textContent = typeof value === 'number' ? `${value.toFixed(1)} fps` : value;
      $('st-fps').append(dt, dd);
    }
  }

  function addLogLine(msg) {
    const log = $('log');
    const atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 4;

    const line = document.createElement('div');
    line.className = msg.level;
    const time = document.createElement('span');
    time.className = 'time';
    time.textContent = msg.time;
    line.append(time, msg.text);
    log.append(line);

    while (log.childElementCount > MAX_LOG_LINES) log.firstElementChild.remove();
    if (atBottom) log.scrollTop = log.scrollHeight;
  }

  function setReconnectEnabled(enabled) {
    const button = $('reconnect-btn');
    button.disabled = !enabled;
    button.title = enabled ? 'Let go of the sensor and open it again' : 'Waiting for the bridge';
  }

  Connection.on('status', showStatus);
  Connection.on('log', addLogLine);
  Connection.on('error', (msg) => {
    addLogLine({ time: '', level: 'error', text: `Page: ${msg.message}` });
    Notice.show(msg.message, 'error');
  });

  Connection.on('open', () => {
    $('log').innerHTML = '';   // the bridge re-sends its recent log on every connect
    setReconnectEnabled(true);
  });

  Connection.on('close', () => {
    setLight(BRIDGE_DOWN.light, BRIDGE_DOWN.title, BRIDGE_DOWN.help);
    setReconnectEnabled(false);
  });

  $('reconnect-btn').addEventListener('click', () => Connection.send({ type: 'sensor.reconnect' }));
})();
