// Hands-free control: with the switch on, holding a hand above your head for 2 seconds acts like pressing
// the main button of the tab on screen (Record or Stop on Motion capture, Start or Pause on 3D scan).
// Beeps through the PC's speakers confirm each step, so you can work from across the room.
// After a gesture, both hands must come down below the shoulders before another one counts.
'use strict';

const HandsFree = (() => {
  const HOLD_MS = 2000;           // how long the hand must stay up
  const ABOVE_HEAD_M = 0.08;      // how far above the head counts as "up"
  const LOWERED_MS = 400;         // how long hands must be down before the next gesture
  const listeners = [];           // told when the switch changes
  const actions = {};             // tab name -> what the gesture does there

  let enabled = false;
  let audio = null;
  let raisedSince = 0;            // when a hand went up (0 = not up)
  let armed = true;               // false after a gesture, until hands come down
  let loweredSince = 0;

  // ----- Sound -----

  // The browser only allows sound after the person has clicked something, so this starts on the switch's click
  function unlockSound() {
    if (!audio) {
      try { audio = new AudioContext(); } catch { audio = null; }
    }
    if (audio && audio.state === 'suspended') audio.resume();
  }

  function beep(frequency = 880, ms = 160, delayMs = 0) {
    if (!enabled || !audio) return;
    const start = audio.currentTime + delayMs / 1000;
    const tone = audio.createOscillator();
    const volume = audio.createGain();
    tone.frequency.value = frequency;
    volume.gain.setValueAtTime(0.0001, start);
    volume.gain.exponentialRampToValueAtTime(0.25, start + 0.01);
    volume.gain.exponentialRampToValueAtTime(0.0001, start + ms / 1000);
    tone.connect(volume).connect(audio.destination);
    tone.start(start);
    tone.stop(start + ms / 1000 + 0.02);
  }

  // Named sounds, so the tabs say what happened rather than which notes to play
  const sounds = {
    seen: () => beep(660, 120),
    countdown: () => beep(880, 150),
    started: () => { beep(988, 140); beep(1319, 220, 160); },
    stopped: () => { beep(1319, 140); beep(988, 220, 160); },
    saved: () => { beep(784, 120); beep(988, 120, 140); beep(1319, 200, 280); },
    problem: () => beep(262, 600),
  };

  // ----- The gesture -----

  function handUp(body) {
    const j = body.joints;
    if (!j.head) return false;
    const top = j.head.p[1] + ABOVE_HEAD_M;
    return (j.handLeft && j.handLeft.p[1] > top) || (j.handRight && j.handRight.p[1] > top);
  }

  function handsDown(body) {
    const j = body.joints;
    const shoulder = Math.min(j.shoulderLeft ? j.shoulderLeft.p[1] : 9, j.shoulderRight ? j.shoulderRight.p[1] : 9);
    return (!j.handLeft || j.handLeft.p[1] < shoulder) && (!j.handRight || j.handRight.p[1] < shoulder);
  }

  function showProgress(fraction, text) {
    const bar = document.getElementById('handsfree-indicator');
    if (!bar) return;
    bar.hidden = fraction === null;
    if (fraction === null) return;
    bar.querySelector('.fill').style.width = `${Math.round(fraction * 100)}%`;
    bar.querySelector('.label').textContent = text;
  }

  function onSkeletons(msg) {
    const action = actions[Tabs.current];
    if (!enabled || !action) { raisedSince = 0; showProgress(null); return; }
    const now = performance.now();
    const up = msg.bodies.some(handUp);
    const down = msg.bodies.length > 0 && msg.bodies.every(handsDown);

    if (!armed) {
      loweredSince = down ? (loweredSince || now) : 0;
      if (loweredSince && now - loweredSince >= LOWERED_MS) armed = true;
      showProgress(up ? 1 : null, 'Lower your hand to get ready for the next gesture');
      return;
    }
    if (!up) { raisedSince = 0; showProgress(null); return; }
    if (!raisedSince) { raisedSince = now; sounds.seen(); }
    const held = now - raisedSince;
    showProgress(Math.min(1, held / HOLD_MS), `Hold your hand up: ${action.label()}`);
    if (held >= HOLD_MS) {
      armed = false;
      raisedSince = 0;
      loweredSince = 0;
      action.run();
    }
  }

  Connection.on('skeletons', onSkeletons);

  // ----- The switch -----

  function setEnabled(on) {
    enabled = on;
    if (on) unlockSound();
    armed = true;
    raisedSince = 0;
    showProgress(null);
    for (const box of document.querySelectorAll('.handsfree-switch')) box.checked = on;
    for (const listener of listeners) listener(on);
  }

  // A tab registers what the gesture does there: label() describes it, run() does it
  function register(tab, action) {
    actions[tab] = action;
  }

  function onChange(listener) {
    listeners.push(listener);
  }

  for (const box of document.querySelectorAll('.handsfree-switch')) {
    box.addEventListener('change', () => setEnabled(box.checked));
  }
  // Always starts switched off: sound needs a click first, and a surprise start would be unwelcome

  return { register, onChange, sounds, get enabled() { return enabled; } };
})();
