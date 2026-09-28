// The Gigs map: Leaflet with OpenStreetMap tiles, loaded the first time a map is shown. Markers
// are circles, so there are no marker images to host.

const leafletVersion = '1.9.4';
let leaflet;

function loadLeaflet() {
    if (window.L) return Promise.resolve(window.L);
    leaflet ??= new Promise((resolve, reject) => {
        const css = document.createElement('link');
        css.rel = 'stylesheet';
        css.href = `https://cdnjs.cloudflare.com/ajax/libs/leaflet/${leafletVersion}/leaflet.min.css`;
        document.head.appendChild(css);
        const script = document.createElement('script');
        script.src = `https://cdnjs.cloudflare.com/ajax/libs/leaflet/${leafletVersion}/leaflet.min.js`;
        script.onload = () => resolve(window.L);
        script.onerror = () => { leaflet = null; reject(new Error("The map couldn't load. Check your connection.")); };
        document.head.appendChild(script);
    });
    return leaflet;
}

export async function create(element, dotnet, lat, lng, zoom) {
    const L = await loadLeaflet();
    const map = L.map(element).setView([lat, lng], zoom);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
    }).addTo(map);
    map.on('click', e => dotnet.invokeMethodAsync('OnMapClick', e.latlng.lat, e.latlng.lng));
    return { map, dotnet, markers: new Map(), pick: null };
}

function style(kind) {
    return kind === 'gig'
        ? { radius: 9, color: '#b35c00', fillColor: '#ff9f1c', fillOpacity: 0.9, weight: 2 }
        : { radius: 8, color: '#0b5394', fillColor: '#3d85c6', fillOpacity: 0.85, weight: 2 };
}

/** Adds or moves a marker; kind is 'worker' or 'gig'. */
export function put(handle, kind, id, lat, lng, label) {
    const key = kind + ':' + id;
    const existing = handle.markers.get(key);
    if (existing) {
        existing.setLatLng([lat, lng]).setTooltipContent(label);
        return;
    }
    const marker = window.L.circleMarker([lat, lng], style(kind)).addTo(handle.map).bindTooltip(label);
    marker.on('click', e => {
        window.L.DomEvent.stopPropagation(e);
        handle.dotnet.invokeMethodAsync(kind === 'gig' ? 'OnGigClick' : 'OnWorkerClick', id);
    });
    handle.markers.set(key, marker);
}

export function remove(handle, kind, id) {
    const key = kind + ':' + id;
    handle.markers.get(key)?.remove();
    handle.markers.delete(key);
}

/** The spot the person tapped, before they post a gig there. */
export function pick(handle, lat, lng) {
    handle.pick?.remove();
    handle.pick = lat == null ? null : window.L.circleMarker([lat, lng], { radius: 11, color: '#222', fillColor: '#fff', fillOpacity: 0.6, weight: 3 }).addTo(handle.map);
}

export function center(handle, lat, lng, zoom) {
    handle.map.setView([lat, lng], zoom ?? handle.map.getZoom());
}

export function destroy(handle) {
    handle?.map.remove();
}

/** The device's position, or null if location is off or refused. */
export function locate() {
    return new Promise(resolve => {
        if (!navigator.geolocation) return resolve(null);
        navigator.geolocation.getCurrentPosition(
            p => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude }),
            () => resolve(null),
            { enableHighAccuracy: false, timeout: 10000, maximumAge: 60000 });
    });
}
