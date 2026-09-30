// Reads QR codes from the camera with the browser's BarcodeDetector (Chrome and its Android WebView,
// Safari 17+). start() returns false where there's no detector or camera; the page then offers
// typing or pasting the code instead.

export async function start(video, dotnet) {
    if (!('BarcodeDetector' in window) || !navigator.mediaDevices?.getUserMedia) return false;
    const formats = await BarcodeDetector.getSupportedFormats();
    if (!formats.includes('qr_code')) return false;

    const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
    video.srcObject = stream;
    video.setAttribute('playsinline', '');
    await video.play();

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
    return true;
}

export function stop(video) {
    const stream = video?.srcObject;
    if (stream) {
        stream.getTracks().forEach(t => t.stop());
        video.srcObject = null;
    }
}
