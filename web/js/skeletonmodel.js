// The Kinect skeleton's shape: which joints the bones connect, and one colour per person.
// Joint names match the bridge's skeletons message (bridge/Skeleton/Body.cs).
'use strict';

const SkeletonModel = (() => {
  // Each bone joins a joint to the one it hangs from, following the SDK's own hierarchy
  const BONES = [
    ['hipCenter', 'spine'], ['spine', 'shoulderCenter'], ['shoulderCenter', 'head'],
    ['shoulderCenter', 'shoulderLeft'], ['shoulderLeft', 'elbowLeft'], ['elbowLeft', 'wristLeft'], ['wristLeft', 'handLeft'],
    ['shoulderCenter', 'shoulderRight'], ['shoulderRight', 'elbowRight'], ['elbowRight', 'wristRight'], ['wristRight', 'handRight'],
    ['hipCenter', 'hipLeft'], ['hipLeft', 'kneeLeft'], ['kneeLeft', 'ankleLeft'], ['ankleLeft', 'footLeft'],
    ['hipCenter', 'hipRight'], ['hipRight', 'kneeRight'], ['kneeRight', 'ankleRight'], ['ankleRight', 'footRight'],
  ];

  // By player number (1 to 6), bright enough to read over both colour and depth pictures
  const PLAYER_COLOURS = ['#ff4d6d', '#2ec4ff', '#ffd23f', '#6ee26e', '#c77dff', '#ff9f40'];

  function colourFor(player) {
    return PLAYER_COLOURS[(player - 1 + PLAYER_COLOURS.length) % PLAYER_COLOURS.length];
  }

  return { BONES, colourFor };
})();
