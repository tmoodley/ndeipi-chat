// The dev host's browser side. Load it after blazor.webassembly.js (with autostart="false").
//
// The page runs in one of two ways:
// - "?frame" in the URL: the app itself. Blazor starts and renders the app (DevHost.razor), and
//   what happens (API calls, topics, crashes) is posted to the parent page.
// - Otherwise: the dev host's bar and panels, with the app in an iframe. The iframe gives the app a
//   real viewport, so its phone-width CSS applies at phone width. Blazor doesn't start here.

(() => {
  const SOURCE = 'ndeipi-devhost';
  const params = new URLSearchParams(location.search);
  const inApp = params.has('frame');

  const toBase64 = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes)));
  const fromBase64 = text => Uint8Array.from(atob(text), c => c.charCodeAt(0));
  const storage = {
    get: key => { try { return localStorage.getItem(key); } catch { return null; } },
    set: (key, value) => { try { localStorage.setItem(key, value); } catch { } },
    remove: key => { try { localStorage.removeItem(key); } catch { } }
  };

  let app = null;

  window.ndeipiDevHost = {
    storage,

    // ECDSA P-256 / SHA-256, as the web shell does it: base64 SPKI and PKCS#8 keys, raw r||s signatures.
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
      verify: async (publicKeySpki, data, signature) => {
        try {
          const key = await crypto.subtle.importKey('spki', fromBase64(publicKeySpki), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
          return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, fromBase64(signature), data);
        } catch {
          return false;
        }
      }
    },

    connect: dotnet => { app = dotnet; },

    post: message => {
      if (window.parent !== window) window.parent.postMessage({ source: SOURCE, ...message }, location.origin);
      else if (message.type === 'launcher') console.info('[dev host] The app went back to the launcher.');
      else if (message.type === 'error') console.error('[dev host]', message.detail);
    }
  };

  if (inApp) {
    setTheme(params.get('theme'));
    window.addEventListener('message', async e => {
      if (e.origin !== location.origin || e.source !== window.parent || e.data?.source !== SOURCE) return;
      const m = e.data;
      if (m.type === 'theme') setTheme(m.theme);
      if (m.type === 'publish') {
        const error = app ? await app.invokeMethodAsync('Publish', m.topic, m.payload) : 'The app is still starting.';
        window.parent.postMessage({ source: SOURCE, type: 'publish-result', error }, location.origin);
      }
    });
    Blazor.start();
    return;
  }

  document.addEventListener('DOMContentLoaded', chrome);
  if (document.readyState !== 'loading') chrome();

  function setTheme(theme) {
    if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
    else delete document.documentElement.dataset.theme;
  }

  // ---- The dev host's bar and panels ----

  function chrome() {
    if (document.querySelector('.dh')) return;
    const STATE_KEY = 'ndeipi.devhost.chrome';
    const state = Object.assign({ width: 'phone', theme: '', embedded: false, user: null, query: null, tab: 'requests' },
      JSON.parse(storage.get(STATE_KEY) || '{}'));
    const save = () => storage.set(STATE_KEY, JSON.stringify(state));
    let hello = null;
    let topics = new Set();
    let errors = 0;
    let requests = 0;

    setTheme(state.theme);
    document.getElementById('app')?.remove();
    document.body.classList.add('dh-page');
    document.body.insertAdjacentHTML('afterbegin', `
      <div class="dh">
        <header class="dh-bar glass">
          <div class="dh-name"><span class="dh-logo">N</span><strong id="dh-title">Loading…</strong><span class="dh-tag">Dev host</span></div>
          <div class="dh-controls">
            <div class="dh-seg" role="group" aria-label="Screen width">
              <button type="button" data-width="phone">Phone</button>
              <button type="button" data-width="tablet">Tablet</button>
              <button type="button" data-width="desktop">Desktop</button>
            </div>
            <label class="dh-check" title="How the phone app shows it: no shell around the app"><input type="checkbox" id="dh-embedded"> Phone app</label>
            <label class="dh-field">Theme
              <select id="dh-theme"><option value="">Device</option><option value="light">Light</option><option value="dark">Dark</option></select>
            </label>
            <label class="dh-field">Signed in as <select id="dh-user"></select></label>
            <label class="dh-field">Opened with ?<input id="dh-query" placeholder="e.g. chat=…" spellcheck="false"></label>
            <button type="button" class="dh-button" id="dh-reload" title="Restart the app with these settings">Restart</button>
            <a class="dh-button" id="dh-alone" target="_blank" rel="noopener" title="Open the app without the dev host, e.g. on a phone on this network">Open alone ↗</a>
          </div>
        </header>
        <div class="dh-body">
          <div class="dh-stage"><div class="dh-device" id="dh-device"><iframe id="dh-frame" title="The app"></iframe></div></div>
          <aside class="dh-panel glass">
            <div class="dh-tabs" role="tablist">
              <button type="button" role="tab" data-tab="requests">Requests <span id="dh-count-requests" class="dh-count"></span></button>
              <button type="button" role="tab" data-tab="realtime">Realtime <span id="dh-count-topics" class="dh-count"></span></button>
              <button type="button" role="tab" data-tab="log">Log <span id="dh-count-errors" class="dh-count bad"></span></button>
            </div>
            <section data-panel="requests">
              <div class="dh-panel-head"><span>The app's API calls, answered by the mocks in Program.cs.</span><button type="button" class="dh-link" id="dh-clear">Clear</button></div>
              <ol class="dh-requests" id="dh-requests"><li class="dh-none">No calls yet.</li></ol>
            </section>
            <section data-panel="realtime">
              <p class="dh-panel-head" id="dh-realtime-off" hidden>Realtime is off (OfferRealtime = false), so the app gets a null Realtime.</p>
              <h3>Topics the app follows</h3>
              <div class="dh-topics" id="dh-topics"><span class="dh-none">None yet.</span></div>
              <form id="dh-publish" class="dh-publish">
                <label class="dh-field block">Topic<input id="dh-topic" list="dh-topic-list" spellcheck="false" required></label>
                <datalist id="dh-topic-list"></datalist>
                <label class="dh-field block">Payload (JSON)<textarea id="dh-payload" rows="5" spellcheck="false">{ }</textarea></label>
                <div class="dh-row"><button type="submit" class="dh-button primary">Send to the app</button><span id="dh-publish-result" class="dh-muted"></span></div>
              </form>
              <h3>Published</h3>
              <ol class="dh-messages" id="dh-messages"><li class="dh-none">Nothing published yet.</li></ol>
            </section>
            <section data-panel="log">
              <ol class="dh-log" id="dh-log"><li class="dh-none">Nothing yet.</li></ol>
            </section>
          </aside>
        </div>
      </div>`);

    const $ = id => document.getElementById(id);
    const frame = $('dh-frame');
    const esc = text => String(text ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
    const pretty = text => { if (text == null || text === '') return ''; try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; } };
    const time = () => new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const prepend = (list, html, max = 200) => {
      list.querySelector('.dh-none')?.remove();
      list.insertAdjacentHTML('afterbegin', html);
      while (list.children.length > max) list.lastElementChild.remove();
    };

    function frameUrl() {
      const url = new URL(document.baseURI);
      url.search = '';
      url.hash = '';
      url.searchParams.set('frame', '1');
      if (state.user) url.searchParams.set('user', state.user);
      if (state.embedded) url.searchParams.set('embedded', '1');
      if (state.theme) url.searchParams.set('theme', state.theme);
      if (state.query !== null) url.searchParams.set('q', state.query);
      return url.toString();
    }

    function restart(note) {
      topics = new Set();
      renderTopics();
      frame.src = frameUrl();
      $('dh-alone').href = frame.src;
      if (note) log('info', note);
    }

    function setWidth(width) {
      state.width = width;
      save();
      $('dh-device').dataset.width = width;
      document.querySelectorAll('[data-width]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.width === width)));
    }

    function setTab(tab) {
      state.tab = tab;
      save();
      document.querySelectorAll('[data-tab]').forEach(b => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
      document.querySelectorAll('[data-panel]').forEach(p => { p.hidden = p.dataset.panel !== tab; });
      if (tab === 'log') { errors = 0; $('dh-count-errors').textContent = ''; }
    }

    function renderTopics() {
      const list = [...topics];
      $('dh-topics').innerHTML = list.length
        ? list.map(t => `<button type="button" class="dh-chip" data-topic="${esc(t)}">${esc(t)}</button>`).join('')
        : '<span class="dh-none">None yet.</span>';
      $('dh-topic-list').innerHTML = list.map(t => `<option value="${esc(t)}">`).join('');
      $('dh-count-topics').textContent = list.length || '';
      if (!$('dh-topic').value && list.length) $('dh-topic').value = list[0];
    }

    function log(kind, text, detail) {
      prepend($('dh-log'), `<li class="${kind}"><time>${time()}</time><span>${esc(text)}</span>${detail ? `<details><summary>Details</summary><pre>${esc(detail)}</pre></details>` : ''}</li>`);
      if (kind === 'bad' && state.tab !== 'log') $('dh-count-errors').textContent = ++errors;
    }

    // Controls
    document.querySelectorAll('[data-width]').forEach(b => b.addEventListener('click', () => setWidth(b.dataset.width)));
    document.querySelectorAll('[data-tab]').forEach(b => b.addEventListener('click', () => setTab(b.dataset.tab)));
    $('dh-embedded').checked = state.embedded;
    $('dh-embedded').addEventListener('change', e => {
      state.embedded = e.target.checked;
      save();
      restart(state.embedded ? 'Showing it as the phone app does: no shell around it.' : 'Showing it inside the web shell.');
    });
    $('dh-theme').value = state.theme;
    $('dh-theme').addEventListener('change', e => {
      state.theme = e.target.value;
      save();
      setTheme(state.theme);
      frame.contentWindow?.postMessage({ source: SOURCE, type: 'theme', theme: state.theme }, location.origin);
      $('dh-alone').href = frameUrl();
    });
    $('dh-user').addEventListener('change', e => {
      state.user = e.target.value;
      save();
      restart(`Signed in as ${e.target.selectedOptions[0].textContent}.`);
    });
    $('dh-query').addEventListener('change', e => {
      state.query = e.target.value.replace(/^\?/, '');
      save();
      restart(state.query ? `Opened with ?${state.query}` : 'Opened with no query.');
    });
    $('dh-reload').addEventListener('click', () => restart('Restarted the app.'));
    $('dh-clear').addEventListener('click', () => {
      $('dh-requests').innerHTML = '<li class="dh-none">No calls yet.</li>';
      requests = 0;
      $('dh-count-requests').textContent = '';
    });
    $('dh-topics').addEventListener('click', e => {
      const topic = e.target.closest('[data-topic]')?.dataset.topic;
      if (topic) { $('dh-topic').value = topic; $('dh-payload').focus(); }
    });
    $('dh-requests').addEventListener('click', e => e.target.closest('li[data-call]')?.classList.toggle('open'));
    $('dh-publish').addEventListener('submit', e => {
      e.preventDefault();
      $('dh-publish-result').textContent = 'Sending…';
      frame.contentWindow?.postMessage({ source: SOURCE, type: 'publish', topic: $('dh-topic').value, payload: $('dh-payload').value }, location.origin);
    });

    // What the app says
    window.addEventListener('message', e => {
      if (e.origin !== location.origin || e.source !== frame.contentWindow || e.data?.source !== SOURCE) return;
      const m = e.data;
      switch (m.type) {
        case 'hello':
          hello = m;
          $('dh-title').textContent = m.title;
          document.title = `${m.title} · Dev host`;
          $('dh-user').innerHTML = m.users.map(u => `<option value="${esc(u.id)}">${esc(u.displayName)}</option>`).join('');
          $('dh-user').value = m.user;
          if (state.query === null) $('dh-query').value = m.query;
          $('dh-realtime-off').hidden = m.realtime;
          if (!m.device) log('info', 'Device is off (OfferDevice = false), so the app gets a null Device.');
          break;
        case 'request': {
          const tone = m.status >= 500 ? 'bad' : m.status >= 400 ? 'warn' : 'ok';
          const body = (label, text) => text ? `<div class="dh-body-label">${label}</div><pre>${esc(pretty(text))}</pre>` : '';
          prepend($('dh-requests'), `
            <li data-call class="${tone}">
              <div class="dh-call"><span class="dh-method">${esc(m.method)}</span><span class="dh-path">${esc(m.path)}</span><span class="dh-status ${tone}">${m.status}</span><span class="dh-ms">${m.milliseconds} ms</span></div>
              <div class="dh-call-detail">${body('Request', m.requestBody)}${body('Response', m.responseBody) || '<div class="dh-body-label">No response body</div>'}</div>
            </li>`);
          $('dh-count-requests').textContent = ++requests;
          if (m.status === 404 || m.status === 405 || m.status >= 500) {
            let title = '';
            try { title = JSON.parse(m.responseBody).title; } catch { }
            if (title?.startsWith('The dev host') || title?.startsWith('The mock') || m.status === 405) log('bad', title || `${m.method} ${m.path}: ${m.status}`);
          }
          break;
        }
        case 'subscribed':
          topics.add(m.topic);
          renderTopics();
          break;
        case 'unsubscribed':
          topics.delete(m.topic);
          renderTopics();
          break;
        case 'published':
          prepend($('dh-messages'), `<li><div><time>${time()}</time><strong>${esc(m.topic)}</strong>${topics.has(m.topic) ? '' : ' <span class="dh-muted">(nothing follows it)</span>'}</div><pre>${esc(pretty(m.payload))}</pre></li>`, 50);
          break;
        case 'publish-result':
          $('dh-publish-result').textContent = m.error ?? 'Sent.';
          break;
        case 'launcher':
          log('info', 'The app went back to the launcher (NavigateToLauncher). In Ndeipi this closes the app.');
          break;
        case 'error':
          log('bad', m.message, m.detail);
          break;
      }
    });

    setWidth(state.width);
    setTab(state.tab);
    if (state.query !== null) $('dh-query').value = state.query;
    restart();
  }
})();
