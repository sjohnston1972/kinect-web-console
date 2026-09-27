// Shared by every tab that shows pictures: draws JPEG frames from the bridge onto a canvas,
// and counts frame rates and delay.
'use strict';

// Frame rate and delay counters, per stream, shown on the Live and Skeleton tabs
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

const FrameView = (() => {
  // Draws a stream's JPEG frames onto a canvas, but only while the canvas is on screen.
  // Decoding happens off the main thread (createImageBitmap). If a frame arrives while the last
  // is still decoding, only the newest waiting one is kept: the page's own newest-frame rule.
  // Returns a function that tells when the canvas last got a picture (performance.now() time).
  function attach(canvas, streamType, statsName) {
    const context = canvas.getContext('2d');
    let busy = false;
    let waiting = null;
    let lastDrawn = 0;

    async function draw(frame) {
      busy = true;
      try {
        const bitmap = await createImageBitmap(new Blob([frame.payload], { type: 'image/jpeg' }));
        context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        bitmap.close();
        lastDrawn = performance.now();
        LiveStats.tick(statsName, frame.timestamp);
      } catch (err) {
        console.warn(`Could not show a ${statsName} frame`, err);
      }
      busy = false;
      if (waiting) {
        const next = waiting;
        waiting = null;
        draw(next);
      }
    }

    Connection.on(`stream:${streamType}`, (frame) => {
      if (canvas.offsetParent === null) return;   // hidden: another tab is showing this stream
      if (busy) waiting = frame;
      else draw(frame);
    });

    return () => lastDrawn;
  }

  return { attach };
})();
