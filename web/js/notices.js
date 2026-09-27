// Small pop-up messages in the bottom right corner, for things the person should notice now,
// such as a refused tilt or a saved snapshot. They fade after a few seconds.
'use strict';

const Notice = (() => {
  const SHOW_MS = 6000;
  let area = null;

  function show(text, level = 'info') {
    if (!area) {
      area = document.createElement('div');
      area.className = 'notices';
      area.setAttribute('aria-live', 'polite');
      document.body.append(area);
    }
    const note = document.createElement('div');
    note.className = `notice ${level}`;
    note.textContent = text;
    area.append(note);
    setTimeout(() => note.remove(), SHOW_MS);
  }

  return { show };
})();
