# Doctor Tablet Phase 1B-1: single-page ink core

Implemented in `apps/doctor_tablet`. Backend APIs and migrations were not changed by this phase. Existing unrelated workspace changes were preserved.

## Model and coordinates

`InkDocument`, `InkStroke`, and `InkPoint` each declare format version 1. Documents carry both patientId and pageId and immutable lists of vector strokes. Strokes carry an in-memory identifier, ARGB color, logical width, and immutable point samples. Points contain x/y, normalized nullable pressure, and the raw pointer timestamp in microseconds. The document uses a 210 × 297 logical A4 page, mapped to the rendered page rectangle independent of display pixels. There is no raster storage or serialization.

Pressure is normalized from the device's advertised range. Nonfinite values, invalid ranges and out-of-range samples become unavailable. Constant or nearly constant readings, single-point strokes, and strokes containing unavailable samples render at fixed width. Pressure variation must exceed 0.02 before variable width is used. Up-event pressure is not sampled because it can reset on release. This is a conservative software heuristic, not hardware capability detection.

## Input and editing

The page uses Flutter Listener down/move/up/cancel events. Only a stylus can draw; the controller tracks one active pointer. Touch and mouse never create ink or interrupt a pen stroke. A gesture-arena guard prevents canvas contacts from scrolling the enclosing notebook list; it does not generate ink. Finger navigation on the canvas remains reserved for a later phase.

The toolbar provides pen and whole-stroke eraser, 0.35/0.7/1.2 logical-unit widths, black/blue/red, and undo/redo, with selected chips and localized English/Arabic labels. The eraser removes whole strokes intersecting its swept path on release, including crossings between samples and dots. One completed draw or erase is one history operation. Cancellation discards the active operation; new edits clear redo.

## Rendering

Two CustomPainter layers have separate RepaintBoundary widgets. The committed layer listens only for committed edits/history/binding changes; the active overlay listens to its own notifier. Pen movement neither rebuilds nor notifies the committed layer. Completed strokes are replayed as vector line segments with round caps and dots. Resizing can legitimately repaint the page. Active stroke snapshots and rendering still scale with the active stroke's sample count; no hardware latency or long-session performance claim is made.

## Patient/page safeguards

Before rendering, the notebook validates the selected page's patient and checks the live cubit's patient/session ownership and selected page ID. Each pointer event and toolbar action rechecks the live context. The controller independently requires the document's patient/page binding to match the input target. Updates to either identifier discard committed ink, active input, and both history stacks; returning to an earlier page starts empty. There is no cross-page cache. Editing is enabled only for a writable draft in the existing notebook state.

Ink is explicitly labeled temporary, not saved or uploaded. Existing metadata submission/finalization actions do not submit ink. Leaving the page or changing patient discards it.

## Verification

Run from `apps/doctor_tablet` using `C:\src\flutter\bin`:

- `flutter analyze`: passed, no issues.
- `flutter test --timeout 60s`: passed, 64 tests, including 10 new ink tests.

Coverage includes stylus vectors/timing/coordinate conversion, touch and mouse rejection, interleaved finger contacts, separate overlay notifications, patient/page input binding, disabled input, pressure normalization/fallback, commit/cancel, swept whole-stroke erasing, dot erasing and misses, undo/redo, page and patient changes, widget visibility guards, and committed painter stability during pointer movement.

## Limits and phase boundary

Pressure, palm rejection, eraser buttons and latency have **not** been validated on real hardware. Correct pointer classification and pressure ranges depend on the device, OS, and Flutter embedder. Inverted stylus and hardware eraser buttons are unsupported; use the toolbar eraser. Touch suppression is application-level filtering, not a claim of native palm rejection.

No server ink serialization/upload, MessagePack, encrypted local database, autosave, offline queue, conflict UI, multi-page ink notebook, pinch zoom/pan, PDF/export, or native Android bridge was added. Work stops at Phase 1B-1.
