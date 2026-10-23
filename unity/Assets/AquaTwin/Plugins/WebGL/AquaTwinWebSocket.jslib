mergeInto(LibraryManager.library, {
  $AQWebSockets: { next: 1, sessions: {} },
  AQ_WS_Open__deps: ['$AQWebSockets', '$UTF8ToString'],
  AQ_WS_Open: function (urlPointer) {
    var id = AQWebSockets.next++;
    var session = { events: [], bytes: 0, socket: null, disposed: false };
    AQWebSockets.sessions[id] = session;
    function push(item) {
      if (session.disposed) return;
      if (session.events.length >= 256 || session.bytes > 2 * 1024 * 1024) {
        session.events = [{ kind: 'error', message: 'Incoming message queue exceeded its limit. Reduce publish rate.' }];
        session.bytes = 0;
        if (session.socket) session.socket.close();
        return;
      }
      session.events.push(item);
      if (item.data) session.bytes += item.data.length;
    }
    try {
      var ws = new WebSocket(UTF8ToString(urlPointer), 'mqtt');
      session.socket = ws;
      ws.binaryType = 'arraybuffer';
      ws.onopen = function () { push({ kind: 'open' }); };
      ws.onmessage = function (event) {
        if (!(event.data instanceof ArrayBuffer)) {
          push({ kind: 'error', message: 'HiveMQ sent a non-binary WebSocket frame.' }); return;
        }
        var bytes = new Uint8Array(event.data);
        if (bytes.length > 1024 * 1024 + 5) {
          push({ kind: 'error', message: 'MQTT frame exceeds 1 MB.' }); ws.close(); return;
        }
        var parts = [];
        for (var i = 0; i < bytes.length; i += 8192)
          parts.push(String.fromCharCode.apply(null, bytes.subarray(i, i + 8192)));
        push({ kind: 'data', data: btoa(parts.join('')) });
      };
      ws.onerror = function () {
        push({ kind: 'error', message: 'Browser WebSocket failed. Check the broker hostname, WSS port (HiveMQ Cloud normally 8884), /mqtt, TLS and network access. Browsers hide the exact TLS/HTTP failure; see DevTools Console and Network > WS.' });
      };
      ws.onclose = function (event) {
        push({ kind: 'close', message: 'WebSocket closed (code ' + event.code + '). ' + (event.wasClean ? 'The broker closed the session.' : 'The connection ended unexpectedly; inspect DevTools Network > WS.') });
      };
    } catch (error) {
      push({ kind: 'error', message: 'Browser could not create the WebSocket. Check the endpoint and site Content-Security-Policy connect-src.' });
    }
    return id;
  },
  AQ_WS_Send__deps: ['$AQWebSockets'],
  AQ_WS_Send: function (id, pointer, length) {
    var session = AQWebSockets.sessions[id];
    if (!session || session.disposed) return;
    try {
      if (!session.socket || session.socket.readyState !== 1) throw new Error('Socket is not open');
      if (session.socket.bufferedAmount > 1024 * 1024) throw new Error('Send buffer is full');
      session.socket.send(HEAPU8.slice(pointer, pointer + length));
    } catch (error) {
      session.events.push({ kind: 'error', message: 'WebSocket send failed: ' + error.message });
    }
  },
  AQ_WS_Poll__deps: ['$AQWebSockets', '$lengthBytesUTF8', '$stringToUTF8', 'malloc'],
  AQ_WS_Poll: function (id) {
    var session = AQWebSockets.sessions[id];
    if (!session || !session.events.length) return 0;
    var item = session.events.shift();
    if (item.data) session.bytes -= item.data.length;
    var text = JSON.stringify(item), size = lengthBytesUTF8(text) + 1, pointer = _malloc(size);
    stringToUTF8(text, pointer, size);
    return pointer;
  },
  AQ_WS_Release__deps: ['free'],
  AQ_WS_Release: function (pointer) { _free(pointer); },
  AQ_WS_Close__deps: ['$AQWebSockets'],
  AQ_WS_Close: function (id) {
    var session = AQWebSockets.sessions[id];
    if (!session) return;
    session.disposed = true;
    if (session.socket) {
      session.socket.onopen = session.socket.onmessage = session.socket.onerror = session.socket.onclose = null;
      try { session.socket.close(); } catch (error) { }
    }
    delete AQWebSockets.sessions[id];
  }
});
