// Reads QR codes from the camera. Where the browser has a BarcodeDetector (Chrome, and its Android
// WebView) that does the work; elsewhere (Safari on iPhone and Mac, Firefox) frames are drawn to a
// canvas and decoded with jsQR (vendor/jsqr), which is loaded only then. Where there's no camera at
// all, the page offers typing or pasting the code instead.

// Returns 'on' once scanning, 'none' with no camera, or 'tap' if the browser wants a tap first.
export async function start(video, dotnet) {
    if (!navigator.mediaDevices?.getUserMedia) return 'none';
    const read = await reader();
    if (!read) return 'none';

    const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
    video.srcObject = stream;
    video.muted = true;
    video.setAttribute('playsinline', '');
    try {
        await video.play();
    } catch (e) {
        // Some browsers only play video after a tap: say so, and the page offers a button.
        stream.getTracks().forEach(t => t.stop());
        video.srcObject = null;
        if (e.name === 'NotAllowedError') return 'tap';
        throw e;
    }

    let last = '', lastAt = 0;
    const tick = async () => {
        if (video.srcObject !== stream) return;
        try {
            const code = await read(video);
            const now = Date.now();
            // The same code stays in view for a while; report it once every few seconds.
            if (code && (code !== last || now - lastAt > 3000)) {
                last = code;
                lastAt = now;
                await dotnet.invokeMethodAsync('OnScanned', code);
            }
        } catch { }
        setTimeout(tick, 200);
    };
    tick();
    return 'on';
}

export function stop(video) {
    const stream = video?.srcObject;
    if (stream) {
        stream.getTracks().forEach(t => t.stop());
        video.srcObject = null;
    }
}

/** A function from a playing video to the QR code in view (or null), or null if none can be had. */
async function reader() {
    if ('BarcodeDetector' in window) {
        try {
            if ((await BarcodeDetector.getSupportedFormats()).includes('qr_code')) {
                const detector = new BarcodeDetector({ formats: ['qr_code'] });
                return async video => (await detector.detect(video))[0]?.rawValue ?? null;
            }
        } catch { }
    }
    const jsQR = await loadJsQr();
    if (!jsQR) return null;
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('2d', { willReadFrequently: true });
    return async video => {
        if (!video.videoWidth) return null;
        // A smaller frame decodes quicker, and a QR code on a screen is big enough to read at 640px.
        const scale = Math.min(1, 640 / Math.max(video.videoWidth, video.videoHeight));
        canvas.width = Math.round(video.videoWidth * scale);
        canvas.height = Math.round(video.videoHeight * scale);
        context.drawImage(video, 0, 0, canvas.width, canvas.height);
        const image = context.getImageData(0, 0, canvas.width, canvas.height);
        return jsQR(image.data, image.width, image.height, { inversionAttempts: 'dontInvert' })?.data ?? null;
    };
}

let jsQrLoading;

function loadJsQr() {
    if (window.jsQR) return Promise.resolve(window.jsQR);
    jsQrLoading ??= new Promise(resolve => {
        const script = document.createElement('script');
        script.src = new URL('./vendor/jsqr/jsQR.js', import.meta.url).href;
        script.onload = () => resolve(window.jsQR ?? null);
        script.onerror = () => resolve(null);
        document.head.appendChild(script);
    });
    return jsQrLoading;
}
