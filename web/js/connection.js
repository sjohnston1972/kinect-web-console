// The page's one connection to the bridge.
// Opens the WebSocket, turns incoming messages into events by their "type",
// and reconnects by itself if the bridge restarts, like a session that re-establishes after a link flap.
'use strict';

const Connection = (() => {
  const PROTOCOL_VERSION = 1;
  const RETRY_MS = 1000;

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
        emit('binary', event.data);
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

  // Joins only the streams the visible tab needs, like joining a multicast group
  function subscribe(list) {
    streams = list;
    send({ type: 'subscribe', streams });
  }

  return { on, send, subscribe, connect, isOpen };
})();
