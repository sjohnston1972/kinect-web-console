// Tab switching. Each tab lists the streams it needs; switching tabs tells the bridge,
// so only the visible tab's data flows and a hidden tab costs nothing.
// A tab can change its own streams while open (the Live tab does when its view changes).
'use strict';

const Tabs = (() => {
  const streamsFor = {
    live: [],
    skeleton: [],
    mocap: [],
    scan: [],
    status: [],
  };
  const listeners = [];
  let current = null;

  const tabs = document.querySelectorAll('.tabs button');
  const panels = document.querySelectorAll('.tab-panel');

  function show(name) {
    if (!(name in streamsFor)) name = 'live';
    current = name;
    for (const tab of tabs) tab.setAttribute('aria-selected', tab.dataset.tab === name);
    for (const panel of panels) panel.hidden = panel.dataset.panel !== name;
    Connection.subscribe(streamsFor[name]);
    history.replaceState(null, '', `#${name}`);
    for (const listener of listeners) listener(name);
  }

  function setStreams(name, streams) {
    streamsFor[name] = streams;
    if (current === name) Connection.subscribe(streams);
  }

  // Called with the tab name whenever the visible tab changes
  function onShow(listener) {
    listeners.push(listener);
  }

  for (const tab of tabs) tab.addEventListener('click', () => show(tab.dataset.tab));

  function start() {
    show(location.hash.slice(1));
    Connection.connect();
  }

  return { setStreams, onShow, start, get current() { return current; } };
})();
