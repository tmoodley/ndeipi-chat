// CyberWars: GN Mining's browser helpers: centring the battlefield, the minimap's jump, and box selection.

export function centre(viewport, x, y) {
    if (!viewport) return;
    viewport.scrollTo({ left: x - viewport.clientWidth / 2, top: y - viewport.clientHeight / 2, behavior: "instant" });
}

// A tap on the minimap scrolls the battlefield there; the minimap's viewBox is in board pixels.
export function bindMinimap(minimap, viewport) {
    if (!minimap || !viewport || minimap.dataset.bound) return;
    minimap.dataset.bound = "1";
    minimap.addEventListener("click", e => {
        const svg = minimap.querySelector("svg");
        if (!svg) return;
        const box = svg.getBoundingClientRect();
        const vb = svg.viewBox.baseVal;
        const side = Math.min(box.width, box.height);
        const fx = (e.clientX - box.left - (box.width - side) / 2) / side;
        const fy = (e.clientY - box.top - (box.height - side) / 2) / side;
        centre(viewport, vb.x + fx * vb.width, vb.y + fy * vb.height);
    });
}

// Selection on the battlefield (FR-2.3, UI-6): drag with the mouse or one finger to box-select,
// two fingers to pan. A plain tap or click still reaches Blazor's click handler.
export function bindBoard(hit, viewport, dotnet) {
    if (!hit || !viewport || hit.dataset.bound) return;
    hit.dataset.bound = "1";
    const pointers = new Map();
    let start = null, box = null, dragged = false, pan = null;

    const local = e => { const r = hit.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
    const midpoint = () => {
        const [a, b] = [...pointers.values()];
        return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    };
    const clearBox = () => { box?.remove(); box = null; };

    hit.addEventListener("pointerdown", e => {
        if (e.pointerType === "mouse" && e.button !== 0) return;
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (pointers.size === 2) {
            // Two fingers: pan instead of selecting.
            clearBox();
            start = null;
            pan = { at: midpoint(), left: viewport.scrollLeft, top: viewport.scrollTop };
            return;
        }
        start = local(e);
        dragged = false;
        hit.setPointerCapture(e.pointerId);
    });

    hit.addEventListener("pointermove", e => {
        if (!pointers.has(e.pointerId)) return;
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (pan && pointers.size >= 2) {
            const m = midpoint();
            viewport.scrollLeft = pan.left - (m.x - pan.at.x);
            viewport.scrollTop = pan.top - (m.y - pan.at.y);
            return;
        }
        if (!start) return;
        const p = local(e);
        if (!dragged && Math.hypot(p.x - start.x, p.y - start.y) < 10) return;
        dragged = true;
        if (!box) {
            box = document.createElement("div");
            box.className = "cw-boxsel";
            hit.parentElement.appendChild(box);
        }
        Object.assign(box.style, {
            left: Math.min(p.x, start.x) + "px", top: Math.min(p.y, start.y) + "px",
            width: Math.abs(p.x - start.x) + "px", height: Math.abs(p.y - start.y) + "px",
        });
    });

    const end = e => {
        if (!pointers.delete(e.pointerId)) return;
        if (pan) {
            if (pointers.size === 0) pan = null;
            return;
        }
        if (start && dragged) {
            const p = local(e);
            dotnet.invokeMethodAsync("SelectBox", start.x, start.y, p.x, p.y);
            // The drag isn't a tap: keep the click that follows from ordering a move.
            hit.addEventListener("click", ev => { ev.stopPropagation(); ev.preventDefault(); }, { capture: true, once: true });
        }
        clearBox();
        start = null;
    };
    hit.addEventListener("pointerup", end);
    hit.addEventListener("pointercancel", end);
}
