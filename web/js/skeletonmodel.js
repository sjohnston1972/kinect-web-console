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

  // By person number: first red, then blue, yellow, green, purple, orange. Bright enough for both pictures.
  const PERSON_COLOURS = ['#ff4d6d', '#2ec4ff', '#ffd23f', '#6ee26e', '#c77dff', '#ff9f40'];

  function colourFor(person) {
    return PERSON_COLOURS[(person - 1 + PERSON_COLOURS.length) % PERSON_COLOURS.length];
  }

  // A body's person number: 1, 2, ... in the order people appeared (from the bridge).
  // Takes recorded before person numbers existed only have the SDK's player number.
  function personOf(body) {
    return body.person || body.player;
  }

  return { BONES, colourFor, personOf };
})();
