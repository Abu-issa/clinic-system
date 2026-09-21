# Doctor Tablet Phase 1B-2: single-page navigation and performance

Builds on Phase 1B-1, in `apps/doctor_tablet` only. No backend APIs or migrations were changed. Ink remains temporary and is not saved or uploaded.

## Gesture architecture

The canvas Listener routes raw pointer events to independent ink and viewport controllers. One tracked touch pans; two touches use their midpoint and distance to pan and pinch. Additional touches are ignored. There is no rotation, inertia, or mouse navigation. An eager gesture-arena guard prevents the containing notebook list from scrolling underneath canvas contacts. The existing patient header remains outside that scrolling list.

The viewport tracks a single stylus owner. Stylus-down clears touch gesture baselines and freezes navigation; touch events cannot interrupt or change the stroke. Stylus-up or cancellation releases suppression. Contacts overlapping a stylus must lift and start a fresh touch gesture before navigating, avoiding jumps from stale palm positions. Reset and tool changes cannot alter an active pen operation. Toolbar erasing remains available; hardware eraser buttons remain unsupported.

## Transform and coordinates

The paper remains 210 × 297 logical units. A uniform fit scale and centered origin place the paper in a viewport at most 600 logical display pixels high. A separate uniform zoom (0.5–4.0) and translation transform both painter layers together:

`screen = pan + zoom × (fitOrigin + fitScale × pagePoint)`

Raw stylus input uses the inverse of exactly that mapping. Pinch updates preserve the page point under the touch midpoint, except where visibility bounds constrain panning. Pan bounds retain a visible portion of the paper, including on very small viewports. Drawing outside paper does not start a stroke; existing strokes remain clipped to page bounds. View changes never edit, rescale, or replace stored stroke points. The reset action restores the fitted 100% view without changing ink.

Patient/page changes clear ink, undo/redo, contacts, and the view transform. A viewport resize cancels unfinished input and resets the view without changing committed coordinates. Existing live patient/session/page guards still run before accepting input.

## Rendering and performance

Committed and active CustomPainter layers retain separate RepaintBoundary widgets. Navigation rebuilds the transform and navigation controls around a retained paper child. Pen moves notify the active painter directly, avoiding widget rebuilds and committed layer updates. The active buffer appends points in constant time and maintains pressure statistics incrementally; immutable stroke snapshots are made at commit rather than copied for every move. The preview exposes a read-only point view. Flutter coalesces repaint requests into frames.

The page is clipped, viewport dimensions and zoom are bounded, and undo retains at most 100 edits. Active painting still costs O(active point count) per rendered frame; committed replay costs O(total points) when invalidated. Eraser intersection work and total retained ink still grow with page complexity. This phase does not establish unlimited-page or long-session performance guarantees. Flutter may re-rasterize retained layers during scaling according to platform/backend behavior.

English and Arabic controls include navigation guidance, zoom percentage, active-stylus status, and reset. Both status labels share stable layout space so changing status cannot move the input origin. Page geometry is independent of RTL/LTR text direction.

## Tests and hardware limits

Validation from `apps/doctor_tablet` using `C:\src\flutter\bin`:

- `flutter analyze`: passed, no issues.
- `flutter test --timeout 60s`: passed, all 74 tests (10 new Phase 1B-2 tests).

The new suite covers one-finger pan, two-finger focal zoom and pan, zoom bounds, small viewport bounds, inverse coordinate conversion, stylus suppression/cancel/ownership, fresh-touch resume, stable active buffering, zoomed drawing, immutable/aligned ink through navigation, reset, patient/page changes, and English LTR/Arabic RTL. Existing stylus/finger, eraser, history, patient-header and binding tests remain in the suite.

Real-device pressure, palm rejection and latency have **not** been validated. Device/OS pointer classification, contact cancellation, event frequency and Flutter raster performance remain hardware dependent. Software touch suppression is not native palm rejection.

No encrypted local database, autosave, ink synchronization, MessagePack, offline queue, multi-page ink notebook, conflict-resolution UI, hardware eraser button, native Android bridge, or PDF/export was added. Work stops at Phase 1B-2.
