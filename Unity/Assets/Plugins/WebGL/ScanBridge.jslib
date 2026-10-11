// Scan mission -> website: each call delivers one JSON message as a window "scan" event (see city.html).
mergeInto(LibraryManager.library, {
  ScanEmit: function (json) {
    var text = UTF8ToString(json);
    try { window.dispatchEvent(new CustomEvent('scan', { detail: JSON.parse(text) })); }
    catch (e) { console.error('scan event', e, text); }
  },
});
