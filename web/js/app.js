// Tab switching. Each tab lists the streams it needs; switching tabs tells the bridge,
// so only the visible tab's data flows and a hidden tab costs nothing.
'use strict';

(() => {
  // Filled in as each phase adds its streams
  const TAB_STREAMS = {
    live: [],
    skeleton: [],
    mocap: [],
    scan: [],
    status: [],
  };

  const tabs = document.querySelectorAll('.tabs button');
  const panels = document.querySelectorAll('.tab-panel');

  function show(name) {
    if (!(name in TAB_STREAMS)) name = 'live';
    for (const tab of tabs) tab.setAttribute('aria-selected', tab.dataset.tab === name);
    for (const panel of panels) panel.hidden = panel.dataset.panel !== name;
    Connection.subscribe(TAB_STREAMS[name]);
    history.replaceState(null, '', `#${name}`);
  }

  for (const tab of tabs) tab.addEventListener('click', () => show(tab.dataset.tab));

  show(location.hash.slice(1));
  Connection.connect();
})();
