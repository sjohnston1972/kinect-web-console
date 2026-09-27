// The page's one connection to the bridge.
// Opens the WebSocket, turns incoming messages into events by their "type",
// and reconnects by itself if the bridge restarts, like a session that re-establishes after a link flap.
//
// Binary messages (pictures and depth) become "stream:N" events, where N is the type byte:
// 1 colour, 2 depth view, 3 raw depth, 4 3D scan preview.
'use strict';

const Connection = (() => {
  const PROTOCOL_VERSION = 1;
  const RETRY_MS = 1000;
  const HEADER_BYTES = 9;

  const handlers = {};
  let socket = null;
  let streams = [];   // what the current tab wants, re-sent after every reconnect

  function on(type, handler) {
    (handlers[type] = handlers[type] || []).push(handler);
  }

  function emit(type, data) {
    for (const handler of handlers[type] || []) {
      // One tab's code failing must not stop the others hearing the message
      try { handler(data); } catch (err) { console.error(`Handler for ${type} failed`, err); }
    }
  }

  // Byte 0 is the stream type, bytes 1 to 8 the timestamp (ms, little-endian), the rest the payload
  function handleBinary(buffer) {
    if (buffer.byteLength < HEADER_BYTES) return;
    const view = new DataView(buffer);
    const type = view.getUint8(0);
    const timestamp = Number(view.getBigUint64(1, true));
    emit(`stream:${type}`, { type, timestamp, buffer, payload: new Uint8Array(buffer, HEADER_BYTES) });
  }

  function connect() {
    socket = new WebSocket(`ws://${location.host}/ws`);
    socket.binaryType = 'arraybuffer';

    socket.onopen = () => {
      emit('open');
      send({ type: 'subscribe', streams });
    };

    socket.onmessage = (event) => {
      if (typeof event.data === 'string') {
        let message;
        try { message = JSON.parse(event.data); } catch { return; }
        emit(message.type, message);
      } else {
        handleBinary(event.data);
      }
    };

    socket.onclose = () => {
      emit('close');
      setTimeout(connect, RETRY_MS);
    };
  }

  function isOpen() {
    return socket !== null && socket.readyState === WebSocket.OPEN;
  }

  // Every message carries the protocol version, so an old page and a new bridge notice the mismatch
  function send(message) {
    if (!isOpen()) return false;
    socket.send(JSON.stringify({ ...message, v: PROTOCOL_VERSION }));
    return true;
  }

  // Joins only the streams the visible view needs, like joining a multicast group
  function subscribe(list) {
    streams = list;
    send({ type: 'subscribe', streams });
  }

  return { on, send, subscribe, connect, isOpen, HEADER_BYTES };
})();
