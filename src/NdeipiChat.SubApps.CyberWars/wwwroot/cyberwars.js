// CyberWars: GN Mining's few browser helpers: centring the battlefield, and the minimap's jump.

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
