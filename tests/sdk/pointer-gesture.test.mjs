// The pointer rules of the web viewer's annotation tools (src/PdfViewer.Web/wwwroot/js/pointer-gesture.js,
// used by viewer.js): taps, double-clicks, the end points a finger ends a polygon on, and the finger
// tracker that turns a second finger into a scroll. What the browser does with them (touch-action,
// pointer capture, the pages) is not run here. Run with `node --test tests/sdk`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Loaded as a module from its text, as font-base.test.mjs does.
const moduleText = readFileSync(new URL('../../src/PdfViewer.Web/wwwroot/js/pointer-gesture.js', import.meta.url), 'utf8');
const { FingerTracker, TapSlop, TouchTarget, endPointNear, isDoubleClick, isTap, ownsEvent, polyTap, pressDecision, takesTouch, tapSuppressed } =
    await import('data:text/javascript,' + encodeURIComponent(moduleText));

test('only the Select tool leaves a finger to the browser', () => {
    assert.equal(takesTouch('select'), false);
    for (const tool of ['highlight', 'underline', 'strikeout', 'note', 'freetext', 'callout', 'ink', 'square', 'circle',
        'line', 'arrow', 'polygon', 'polyline', 'stamp', 'customstamp']) assert.equal(takesTouch(tool), true, tool);
});

test('a tap moves no further than the slop on either axis', () => {
    const start = { x: 100, y: 200 };
    assert.equal(TapSlop, 10);
    assert.equal(isTap(start, 100, 200), true);
    assert.equal(isTap(start, 110, 190), true);
    assert.equal(isTap(start, 111, 200), false);
    assert.equal(isTap(start, 100, 189), false);
    assert.equal(isTap(null, 100, 200), false);
    assert.equal(isTap(start, 104, 200, 3), false);
});

test('the mouse double-click rule is the one the viewer had: under 400 ms and under 8 px', () => {
    const last = { time: 1000, x: 50, y: 50 };
    assert.equal(isDoubleClick(null, { time: 1100, x: 50, y: 50 }), false);
    assert.equal(isDoubleClick(last, { time: 1399, x: 53, y: 54 }), true);
    assert.equal(isDoubleClick(last, { time: 1400, x: 50, y: 50 }), false);
    assert.equal(isDoubleClick(last, { time: 1100, x: 54, y: 54 }), false);   // 8 px across plus down
});

test('a finger ends a shape on its first or last point, within half a touch target', () => {
    assert.equal(TouchTarget, 44);
    // A page shown 400 x 800 CSS px; points at (40, 80), (200, 400) and (360, 720) px.
    const points = [0.1, 0.1, 0.5, 0.5, 0.9, 0.9];
    assert.equal(endPointNear(points, 400, 800, 0.1, 0.1), 0);
    assert.equal(endPointNear(points, 400, 800, 0.9 + 21 / 400, 0.9), 2);
    assert.equal(endPointNear(points, 400, 800, 0.9 + 23 / 400, 0.9), -1);
    assert.equal(endPointNear(points, 400, 800, 0.5, 0.5), -1);              // a middle point is not an end
    assert.equal(endPointNear(points, 400, 800, 0.1, 0.1 + 15 / 800), 0);
    assert.equal(endPointNear([0.5, 0.5], 400, 800, 0.5, 0.5 + 10 / 800), 0);
    assert.equal(endPointNear([], 400, 800, 0.5, 0.5), -1);
    assert.equal(endPointNear(points, 0, 800, 0.1, 0.1), -1);
    // Both ends close together: the nearer one.
    assert.equal(endPointNear([0.5, 0.5, 0.2, 0.2, 0.52, 0.5], 400, 800, 0.515, 0.5), 2);
});

test('a tap on an end finishes a polygon from 3 points and a polyline from 2; before that it adds nothing', () => {
    const one = [0.1, 0.1], two = [0.1, 0.1, 0.5, 0.1], three = [0.1, 0.1, 0.5, 0.1, 0.5, 0.5];
    assert.equal(polyTap('polygon', one, 400, 400, 0.1, 0.1), 'ignore');
    assert.equal(polyTap('polygon', two, 400, 400, 0.1, 0.1), 'ignore');
    assert.equal(polyTap('polygon', three, 400, 400, 0.1, 0.1), 'finish');
    assert.equal(polyTap('polygon', three, 400, 400, 0.5, 0.5), 'finish');   // the last point again: a double-tap
    assert.equal(polyTap('polygon', three, 400, 400, 0.9, 0.9), 'add');
    assert.equal(polyTap('polyline', one, 400, 400, 0.1, 0.1), 'ignore');
    assert.equal(polyTap('polyline', two, 400, 400, 0.5, 0.1), 'finish');
    assert.equal(polyTap('polyline', two, 400, 400, 0.3, 0.3), 'add');
});

test('mouse and pen presses are never a gesture', () => {
    const f = new FingerTracker();
    assert.equal(f.down(1, 'mouse', 0, 0, true), 'single');
    assert.equal(f.down(2, 'pen', 0, 0, true), 'single');
    assert.equal(f.count, 0);
    assert.equal(f.gesture, false);
    assert.equal(f.move(1, 10, 10), null);
});

test('a second finger makes a gesture until the last finger is up; the fingers\' centre scrolls', () => {
    const f = new FingerTracker();
    assert.equal(f.down(1, 'touch', 100, 100, true), 'single');
    assert.equal(f.move(1, 110, 100), null);               // one finger draws, it does not scroll
    assert.equal(f.down(2, 'touch', 200, 100, false), 'gesture');
    assert.equal(f.gesture, true);
    assert.deepEqual(f.move(1, 110, 140), [0, 20]);        // one of two fingers moved 40 px: the centre 20 px
    assert.deepEqual(f.move(2, 200, 140), [0, 20]);
    f.up(1);
    assert.equal(f.gesture, true);                         // the finger left does not start a stroke
    assert.equal(f.move(2, 200, 180), null);               // nor scroll alone
    assert.equal(f.down(3, 'touch', 50, 50, false), 'gesture');
    f.up(2);
    f.up(3);
    assert.equal(f.gesture, false);
    assert.equal(f.count, 0);
    assert.equal(f.down(4, 'touch', 0, 0, true), 'single');
});

test('a primary finger starts afresh when a release was never seen', () => {
    const f = new FingerTracker();
    f.down(1, 'touch', 0, 0, true);
    f.down(2, 'touch', 0, 0, false);                       // their pointerup went to an element that was removed
    assert.equal(f.down(3, 'touch', 0, 0, true), 'single');
    assert.equal(f.count, 1);
    f.up(99);                                              // an unknown pointer
    assert.equal(f.count, 1);
    assert.equal(f.move(99, 1, 1), null);
});

test('one action at a time: a palm is ignored while a pen or a mouse draws; a pen takes over from a finger', () => {
    assert.equal(pressDecision(null, 'touch', true), 'take');
    assert.equal(pressDecision(null, 'pen', true), 'take');
    assert.equal(pressDecision(null, 'mouse', true), 'take');
    const pen = { id: 2, type: 'pen' }, mouse = { id: 1, type: 'mouse' }, finger = { id: 7, type: 'touch' };
    assert.equal(pressDecision(pen, 'touch', true), 'ignore');      // the palm under the hand
    assert.equal(pressDecision(pen, 'touch', false), 'ignore');
    assert.equal(pressDecision(mouse, 'touch', true), 'ignore');
    assert.equal(pressDecision(finger, 'pen', true), 'replace');     // the pen wins over a finger
    assert.equal(pressDecision(finger, 'mouse', true), 'replace');
    assert.equal(pressDecision(finger, 'touch', false), 'take');     // a second finger: FingerTracker makes it a gesture
    assert.equal(pressDecision(finger, 'touch', true), 'replace');   // a new first finger: the old release was never seen
    assert.equal(pressDecision(pen, 'pen', true), 'replace');
});

test('moves, releases and cancels of another pointer do not touch the action in progress', () => {
    assert.equal(ownsEvent(null, 5), true);
    assert.equal(ownsEvent({ id: 5, type: 'touch' }, 5), true);
    assert.equal(ownsEvent({ id: 5, type: 'touch' }, 1), false);    // a hovering mouse while a finger inks
    assert.equal(ownsEvent({ id: 2, type: 'pen' }, 9), false);      // a palm while a pen draws
});

test('a tap within 400 ms of a finger ending a shape is dropped', () => {
    assert.equal(tapSuppressed(null, 1000), false);
    assert.equal(tapSuppressed(1000, 1000), true);
    assert.equal(tapSuppressed(1000, 1399), true);
    assert.equal(tapSuppressed(1000, 1400), false);
    assert.equal(tapSuppressed(1000, 900), false);                 // a time stamp before it: not the second tap
    assert.equal(tapSuppressed(1000, 1100, 50), false);
});
