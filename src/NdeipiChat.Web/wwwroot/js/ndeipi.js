// Small browser helpers the Blazor app calls through JS interop.
(() => {
  const toBase64 = (buffer) => {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000)
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(binary);
  };
  const fromBase64 = (text) => Uint8Array.from(atob(text), c => c.charCodeAt(0));

  // Livestock captures waiting to upload: one IndexedDB record each, photos included, so they
  // survive reloads and closing the tab -- the web app's version of the phone's SQLite queue.
  let database;
  const openDatabase = () => database ??= new Promise((resolve, reject) => {
    const request = indexedDB.open('ndeipi-livestock', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('captures', { keyPath: 'id' });
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => { database = undefined; reject(request.error); };
  });
  const withStore = async (mode, work) => {
    const db = await openDatabase();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction('captures', mode);
      let result;
      const request = work(transaction.objectStore('captures'));
      if (request) request.onsuccess = () => { result = request.result; };
      transaction.oncomplete = () => resolve(result);
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error);
    });
  };
  const withoutPhotos = (record) => {
    if (!record) return null;
    const { face, flank, ...rest } = record;
    return rest;
  };

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

    // A photo on screen without copying megabytes into the page as base64.
    objectUrl: (bytes, type) => URL.createObjectURL(new Blob([bytes], { type: type || 'image/jpeg' })),
    revokeObjectUrl: (url) => URL.revokeObjectURL(url),

    // The browser's position for a registration; null if it's off, refused or times out.
    location: () => new Promise(resolve => {
      if (!navigator.geolocation) { resolve(null); return; }
      navigator.geolocation.getCurrentPosition(
        p => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude, accuracyMeters: p.coords.accuracy }),
        () => resolve(null),
        { enableHighAccuracy: true, timeout: 20000, maximumAge: 60000 });
    }),

    // ECDSA P-256 / SHA-256 through Web Crypto. Keys go out as base64 SPKI and PKCS#8; signatures
    // are raw r||s, the same form .NET's ECDsa produces and the API verifies.
    p256: {
      createKey: async () => {
        const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
        return [
          toBase64(await crypto.subtle.exportKey('spki', pair.publicKey)),
          toBase64(await crypto.subtle.exportKey('pkcs8', pair.privateKey))
        ];
      },
      sign: async (privateKeyPkcs8, data) => {
        const key = await crypto.subtle.importKey('pkcs8', fromBase64(privateKeyPkcs8), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign']);
        return toBase64(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key, data));
      }
    },

    captures: {
      put: (capture, face, flank) => withStore('readwrite', store => store.put({ ...capture, face, flank })),
      update: async (capture) => {
        const existing = await withStore('readonly', store => store.get(capture.id));
        if (existing) await withStore('readwrite', store => store.put({ ...existing, ...capture }));
      },
      get: async (id) => withoutPhotos(await withStore('readonly', store => store.get(id))),
      list: async () => (await withStore('readonly', store => store.getAll()) || []).map(withoutPhotos),
      photo: async (id, which) => (await withStore('readonly', store => store.get(id)))?.[which] ?? null,
      remove: (id) => withStore('readwrite', store => store.delete(id))
    }
  };
})();
