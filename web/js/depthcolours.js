// The depth colour scale: near is warm, far is cool.
// These stops must match bridge/Streams/DepthColouriser.cs, which paints the depth pictures.
// Used here for the legend and for colouring the 3D point cloud.
'use strict';

const DepthColours = (() => {
  const MIN_MM = 800;
  const MAX_MM = 4000;
  const STOPS = [
    [0.0, [255, 70, 40]],
    [0.2, [255, 150, 30]],
    [0.4, [240, 225, 60]],
    [0.6, [70, 200, 120]],
    [0.8, [40, 160, 220]],
    [1.0, [70, 70, 200]],
  ];

  // Colour for a distance in millimetres, as [r, g, b] from 0 to 255
  function rgb(mm) {
    const t = Math.min(1, Math.max(0, (mm - MIN_MM) / (MAX_MM - MIN_MM)));
    let i = 0;
    while (i < STOPS.length - 2 && t > STOPS[i + 1][0]) i++;
    const [t0, c0] = STOPS[i];
    const [t1, c1] = STOPS[i + 1];
    const f = (t - t0) / (t1 - t0);
    const c = c0.map((v, k) => v + f * (c1[k] - v));
    // Outside the reliable range is shown dimmed, as on the bridge
    return mm < MIN_MM || mm > MAX_MM ? c.map((v) => v / 2) : c;
  }

  // Lookup table of 0 to 1 colours for every millimetre, used by the point cloud 30 times a second
  const lookup = new Float32Array(8192 * 3);
  for (let mm = 1; mm < 8192; mm++) {
    const [r, g, b] = rgb(mm);
    lookup[mm * 3] = r / 255;
    lookup[mm * 3 + 1] = g / 255;
    lookup[mm * 3 + 2] = b / 255;
  }

  // A CSS gradient for the legend, running near (left) to far (right)
  function cssGradient() {
    return `linear-gradient(to right, ${STOPS.map(([t, c]) => `rgb(${c.join(',')}) ${t * 100}%`).join(', ')})`;
  }

  return { MIN_MM, MAX_MM, rgb, lookup, cssGradient };
})();
