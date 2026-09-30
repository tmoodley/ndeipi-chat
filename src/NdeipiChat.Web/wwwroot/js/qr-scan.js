// Reads QR codes from the camera with the browser's BarcodeDetector (Chrome and its Android WebView,
// Safari 17+). Where there's no detector or camera, the page offers
// typing or pasting the code instead.

// Returns 'on' once scanning, 'none' with no detector or camera, or 'tap' if the browser wants a tap first.
export async function start(video, dotnet) {
    if (!('BarcodeDetector' in window) || !navigator.mediaDevices?.getUserMedia) return 'none';
    const formats = await BarcodeDetector.getSupportedFormats();
    if (!formats.includes('qr_code')) return 'none';

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

    const detector = new BarcodeDetector({ formats: ['qr_code'] });
    let last = '', lastAt = 0;
    const tick = async () => {
        if (video.srcObject !== stream) return;
        try {
            for (const code of await detector.detect(video)) {
                const now = Date.now();
                // The same code stays in view for a while; report it once every few seconds.
                if (code.rawValue !== last || now - lastAt > 3000) {
                    last = code.rawValue;
                    lastAt = now;
                    await dotnet.invokeMethodAsync('OnScanned', code.rawValue);
                }
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
