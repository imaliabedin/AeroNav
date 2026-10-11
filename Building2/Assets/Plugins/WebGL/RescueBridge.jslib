// Rescue Mission -> website: each call delivers one JSON message as a window "rescue" event (see rescue.html).
mergeInto(LibraryManager.library, {
  RescueEmit: function (json) {
    var text = UTF8ToString(json);
    try { window.dispatchEvent(new CustomEvent('rescue', { detail: JSON.parse(text) })); }
    catch (e) { console.error('rescue event', e, text); }
  },
});
