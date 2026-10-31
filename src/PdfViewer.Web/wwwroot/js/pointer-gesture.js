// The pointer rules of the annotation tools that do not need a page: when a press is a tap, when two
// presses are a double-click, which polygon point a finger tapped, and the fingers on the pages.
// viewer.js uses them for the mouse, the pen and touch (Pointer Events); tests/sdk/pointer-gesture.test.mjs
// runs them in Node. The page's touch-action (app.css) decides what the browser does with a finger:
// with the Select tool it scrolls and zooms; with any other tool the viewer takes the finger.

/** CSS pixels a finger may move and still tap. */
export const TapSlop = 10;

/** The smallest touch target, in CSS pixels: a finger tapped a point when it is within half of it. */
export const TouchTarget = 44;

/** A tool takes a finger on a page (it draws, places or marks text); only Select leaves it to the browser. */
export function takesTouch(tool) {
    return tool !== 'select';
}

/** A press at start (clientX, clientY) that ends at (x, y) is a tap: it did not move further than the slop. */
export function isTap(start, x, y, slop = TapSlop) {
    return !!start && Math.abs(x - start.x) <= slop && Math.abs(y - start.y) <= slop;
}

/**
 * The second press of a mouse or pen double-click: within 400 ms of the last one and less than 8 px
 * (across plus down) from it. last and now: { time, x, y } (event time stamp, client pixels).
 */
export function isDoubleClick(last, now) {
    return !!last && now.time - last.time < 400 && Math.abs(now.x - last.x) + Math.abs(now.y - last.y) < 8;
}

/**
 * The point of points ([x0, y0, x1, y1, …], fractions of the page) that (x, y) is within radius CSS
 * pixels of, on a page shown width x height CSS pixels: the first one or the last one (the two a
 * finger ends a shape on), the nearer of the two; -1 when neither.
 */
export function endPointNear(points, width, height, x, y, radius = TouchTarget / 2) {
    const n = Math.floor(points.length / 2);
    if (n === 0 || !(width > 0) || !(height > 0)) return -1;
    let best = -1, distance = radius;
    for (const i of n === 1 ? [0] : [0, n - 1]) {
        const d = Math.hypot((points[2 * i] - x) * width, (points[2 * i + 1] - y) * height);
        if (d <= distance) { best = i; distance = d; }
    }
    return best;
}

/**
 * What a finger's tap at (x, y) does to a polygon or polyline being drawn (points as for
 * endPointNear): 'finish' on its first or last point once the shape has enough points (a polygon 3,
 * a polyline 2), 'ignore' on them before that (no point twice), else 'add'.
 */
export function polyTap(tool, points, width, height, x, y) {
    if (endPointNear(points, width, height, x, y) < 0) return 'add';
    return points.length / 2 >= (tool === 'polygon' ? 3 : 2) ? 'finish' : 'ignore';
}

/**
 * The fingers on the pages. One finger draws; a second one makes it a gesture until every finger is
 * up: the stroke or shape being drawn is dropped and the fingers scroll the pages together.
 * Mouse and pen presses are not counted: they are never more than one.
 */
export class FingerTracker {
    constructor() {
        this.fingers = new Map();   // pointerId -> { x, y }
        this.gesture = false;       // two fingers were down since the first one
    }

    get count() { return this.fingers.size; }

    /**
     * A pointer went down. Returns 'single' for the press that may draw, 'gesture' when it made
     * (or is part of) a gesture of two or more fingers. A primary touch starts afresh: a finger
     * whose release the page never saw (its element went away) is not counted for ever.
     */
    down(id, type, x, y, isPrimary) {
        if (type !== 'touch') return 'single';
        if (isPrimary) { this.fingers.clear(); this.gesture = false; }
        this.fingers.set(id, { x, y });
        if (this.fingers.size >= 2) this.gesture = true;
        return this.gesture ? 'gesture' : 'single';
    }

    /** A finger moved: the distance [dx, dy] the fingers' centre moved while in a gesture, else null. */
    move(id, x, y) {
        const f = this.fingers.get(id);
        if (!f) return null;
        const n = this.fingers.size;
        const dx = x - f.x, dy = y - f.y;
        f.x = x;
        f.y = y;
        return this.gesture && n >= 2 ? [dx / n, dy / n] : null;
    }

    /** A pointer went up or was cancelled. The gesture ends with the last finger. */
    up(id) {
        this.fingers.delete(id);
        if (this.fingers.size === 0) this.gesture = false;
    }
}

/**
 * Which pointer an action belongs to. owner: { id, type } of the pointer that began the action in
 * progress (a stroke, a dragged shape, a move, a text selection, a finger's tap), or null. Returns
 * what a new press of pointer type `type` does:
 * - 'take': nothing is in progress (or it is a further finger, which FingerTracker makes a gesture);
 * - 'ignore': a finger while a pen or a mouse draws (a palm resting on the screen);
 * - 'replace': a pen or a mouse while a finger draws (the pen wins), or a new first finger or a
 *   second mouse or pen press while the owner's release was never seen; what the owner began is dropped.
 */
export function pressDecision(owner, type, isPrimary) {
    if (!owner) return 'take';
    if (type === 'touch') {
        if (owner.type !== 'touch') return 'ignore';
        return isPrimary ? 'replace' : 'take';
    }
    return 'replace';
}

/** A move, release or cancel of pointer id belongs to the action in progress (or there is none). */
export function ownsEvent(owner, id) {
    return !owner || owner.id === id;
}

/**
 * A finger's tap within ms of a finger ending a polygon or polyline (at finishedAt, event time
 * stamps) is dropped: it is the second tap of a double-tap whose first tap already ended the shape,
 * and must not begin a new one.
 */
export function tapSuppressed(finishedAt, now, ms = 400) {
    return typeof finishedAt === 'number' && now >= finishedAt && now - finishedAt < ms;
}
