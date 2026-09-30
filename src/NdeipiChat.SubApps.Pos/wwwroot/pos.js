// The till's browser side: locking after inactivity (FR-AUTH-02), noticing the connection come and
// go (FR-OFF-01), camera barcode scanning (FR-CAT-01, FR-HDW-02), a scan beep, and printing the
// receipt through the device's print dialog (80 mm thermal printers included).

let idle;

/** Calls dotnet.Lock() after `seconds` without a touch or key press; watch again to reset. */
export function watchIdle(dotnet, seconds) {
    stopIdle();
    const reset = () => {
        clearTimeout(idle?.timer);
        idle.timer = setTimeout(() => dotnet.invokeMethodAsync('Lock'), seconds * 1000);
    };
    idle = { reset, timer: 0 };
    for (const e of ['pointerdown', 'keydown']) window.addEventListener(e, reset, { passive: true });
    reset();
}

export function stopIdle() {
    if (!idle) return;
    clearTimeout(idle.timer);
    for (const e of ['pointerdown', 'keydown']) window.removeEventListener(e, idle.reset);
    idle = null;
}

let connection;

/** Tells dotnet.Connection(online) when the browser goes on or offline; returns whether it's online now. */
export function watchConnection(dotnet) {
    unwatchConnection();
    const on = () => dotnet.invokeMethodAsync('Connection', true);
    const off = () => dotnet.invokeMethodAsync('Connection', false);
    window.addEventListener('online', on);
    window.addEventListener('offline', off);
    connection = { on, off };
    return navigator.onLine;
}

export function unwatchConnection() {
    if (!connection) return;
    window.removeEventListener('online', connection.on);
    window.removeEventListener('offline', connection.off);
    connection = null;
}

/** A short beep: the till heard the scan. */
export function beep() {
    try {
        const audio = new (window.AudioContext || window.webkitAudioContext)();
        const tone = audio.createOscillator();
        const gain = audio.createGain();
        tone.frequency.value = 1400;
        gain.gain.value = 0.08;
        tone.connect(gain).connect(audio.destination);
        tone.start();
        tone.stop(audio.currentTime + 0.08);
        tone.onended = () => audio.close();
    } catch { }
}

let scanner;

/** Whether this browser can read barcodes with the camera. */
export function canScan() {
    return 'BarcodeDetector' in window && !!navigator.mediaDevices?.getUserMedia;
}

/**
 * Reads barcodes from the back camera into `video`, calling dotnet.Scanned(code) for each new one,
 * until stopScan(). EAN/UPC, Code 128 and 39, ITF, DataMatrix and QR.
 */
export async function startScan(video, dotnet) {
    await stopScan();
    const detector = new BarcodeDetector({ formats: ['ean_13', 'ean_8', 'upc_a', 'upc_e', 'code_128', 'code_39', 'itf', 'data_matrix', 'qr_code'] });
    const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
    video.srcObject = stream;
    video.setAttribute('playsinline', '');
    await video.play();
    scanner = { stream, running: true, last: '', lastAt: 0 };
    const state = scanner;
    const tick = async () => {
        if (!state.running) return;
        try {
            const codes = await detector.detect(video);
            const code = codes[0]?.rawValue;
            // The same code held in view counts once every two seconds.
            if (code && (code !== state.last || Date.now() - state.lastAt > 2000)) {
                state.last = code;
                state.lastAt = Date.now();
                await dotnet.invokeMethodAsync('Scanned', code);
            }
        } catch { }
        if (state.running) setTimeout(tick, 150);
    };
    tick();
}

export async function stopScan() {
    if (!scanner) return;
    scanner.running = false;
    scanner.stream.getTracks().forEach(t => t.stop());
    scanner = null;
}

/** Prints the receipt on screen (the page's print styles show only it). */
export function printReceipt() {
    document.body.classList.add('pos-printing');
    const done = () => { document.body.classList.remove('pos-printing'); window.removeEventListener('afterprint', done); };
    window.addEventListener('afterprint', done);
    window.print();
    setTimeout(done, 3000);
}

/** A name for this device in the audit trail, e.g. "Android · Chrome". */
export function terminalName() {
    const ua = navigator.userAgent;
    const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac/.test(ua) ? 'Mac' : 'Device';
    const app = /NdeipiApp/.test(ua) ? 'Ndeipi app' : /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : /Firefox\//.test(ua) ? 'Firefox' : 'Browser';
    return `${os} · ${app}`;
}
