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
    // "light", "dark", or null to follow the device; applied before Blazor starts (index.html).
    theme: {
        get: () => { try { return localStorage.getItem('ndeipi.theme'); } catch { return null; } },
        set: (value) => {
            try { value ? localStorage.setItem('ndeipi.theme', value) : localStorage.removeItem('ndeipi.theme'); } catch { }
            if (value) document.documentElement.dataset.theme = value; else delete document.documentElement.dataset.theme;
        },
        isDark: () => document.documentElement.dataset.theme === 'dark'
            || (!document.documentElement.dataset.theme && window.matchMedia('(prefers-color-scheme: dark)').matches)
    },
    storage: {
      get: (area, key) => { try { return window[area].getItem(key); } catch { return null; } },
      set: (area, key, value) => { try { window[area].setItem(key, value); } catch { } },
      remove: (area, key) => { try { window[area].removeItem(key); } catch { } }
    },
    matches: (query) => window.matchMedia(query).matches,
    alert: (title, message) => window.alert(title ? title + '\n\n' + message : message),
    // True only inside the Ndeipi app's WebView, which adds this token to its user agent.
    isInApp: (token) => navigator.userAgent.includes(token),
    fragment: () => window.location.hash,
    open: (url) => window.open(url, '_blank', 'noopener'),
    back: () => window.history.back(),
    scrollToBottom: (element) => { if (element) element.scrollTop = element.scrollHeight; },

    // A post's photo, shrunk in the browser before upload: upright, at most maxEdge on its long side,
    // as JPEG. Saves mobile data, and turns WebP (and HEIC, where the browser reads it) into JPEG.
    shrinkImage: async (bytes, maxEdge, quality) => {
      const bitmap = await createImageBitmap(new Blob([bytes]), { imageOrientation: 'from-image' });
      const scale = Math.min(1, maxEdge / Math.max(bitmap.width, bitmap.height));
      const canvas = document.createElement('canvas');
      canvas.width = Math.round(bitmap.width * scale);
      canvas.height = Math.round(bitmap.height * scale);
      canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
      bitmap.close();
      const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', quality));
      return new Uint8Array(await blob.arrayBuffer());
    },

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
      },
      // Sub-app signatures (a publisher's key, not the operator's). False for anything malformed.
      verify: async (publicKeySpki, data, signature) => {
        try {
          const key = await crypto.subtle.importKey('spki', fromBase64(publicKeySpki), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
          return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, fromBase64(signature), data);
        } catch {
          return false;
        }
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

// The part of the screen actually visible: iOS Safari's keyboard and floating toolbar don't
// resize the page, so full-screen views (a chat) size and place themselves with these instead.
(function () {
    const vv = window.visualViewport;
    if (!vv) return;
    const root = document.documentElement;
    const update = () => {
        root.style.setProperty('--vvh', vv.height + 'px');
        root.style.setProperty('--vvt', vv.offsetTop + 'px');
        root.classList.toggle('kb-open', window.innerHeight - vv.height > 120);
    };
    vv.addEventListener('resize', update);
    vv.addEventListener('scroll', update);
    update();
})();
