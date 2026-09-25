// Small browser helpers the Blazor app calls through JS interop.
window.ndeipi = {
  storage: {
    get: (area, key) => { try { return window[area].getItem(key); } catch { return null; } },
    set: (area, key, value) => { try { window[area].setItem(key, value); } catch { } },
    remove: (area, key) => { try { window[area].removeItem(key); } catch { } }
  },
  alert: (title, message) => window.alert(title ? title + '\n\n' + message : message),
  open: (url) => window.open(url, '_blank', 'noopener'),
  back: () => window.history.back(),
  scrollToBottom: (element) => { if (element) element.scrollTop = element.scrollHeight; },
  isNearBottom: (element) => !element || element.scrollHeight - element.scrollTop - element.clientHeight < 80
};
