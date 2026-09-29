# Doctor Tablet Phase 2D — dense ink remediation and acceptance

Scope: rendering and acceptance only. No backend change, database migration, clinical feature, native bridge, source-vector simplification, or change to encryption/validation/sync contracts in this phase. The working tree also contains earlier Phase 2B/2C work.

## Reproduction

Synthetic fixture: 1,000 strokes, 50,000 pressure-bearing points, MessagePack v2 payload 1,590,742 bytes. Windows host, Flutter 3.47.5 / Dart 3.13.4, Pixel_Tablet Android 15/API 35 x64 emulator. Base commit `3f49994663be777dedf5cfbef4d6b8a0c3ace8f6`, with uncommitted phase changes. These are emulator/host measurements, not physical stylus latency evidence.

The paired integration benchmark renders the original vector painter and the cached painter in the **same build/process**, then performs 20 scale/pan changes at a 600 × 848 logical surface. It records Flutter FrameTiming build/raster percentiles, frames over a nominal 16.67 ms reference, cold setup, and process RSS delta. Timing sample counts include framework pumps; this is not an actual dropped-frame count. Original cold time ends when the initial widget pump returns; cached cold time additionally waits for the derived surface to become available. Neither is a measured pen-to-photon latency, and these different readiness milestones must not be compared as first-visible-frame times. The modes are separate runs; do not compare their absolute values as if host load were controlled. A negative RSS delta reflects GC/process allocation changes, not negative cache memory.

| Mode/path | Samples | Build p50/p95 ms | Raster p50/p95 ms | >16.67 ms | Cold ms | RSS delta bytes |
|---|---:|---:|---:|---:|---:|---:|
| Debug/original | 41 | 6.452 / 99.771 | 1880.575 / 3932.975 | 41 | 1344 | 224251904 |
| Debug/cache | 43 | 8.328 / 45.762 | 42.615 / 87.936 | 43 | 9349 | -141004800 |
| Profile/original | 39 | 2.234 / 33.266 | 3070.185 / 4643.455 | 39 | 404 | 170999808 |
| Profile/cache | 43 | 1.708 / 6.534 | 55.953 / 76.628 | 43 | 11735 | -165040128 |
| Debug repeat/original | 41 | 5.865 / 30.188 | 1836.620 / 2153.660 | 41 | 474 | 186429440 |
| Debug repeat/cache | 43 | 12.688 / 71.591 | 63.868 / 120.908 | 43 | 13697 | -168103936 |

The paired debug raster p95 improves by about 44.7×, but all measured frames still exceed the nominal 60 Hz budget. This is material remediation, **not** a smoothness or rollout acceptance claim. The prior Phase 2C debug p95 (~2197 ms) is historical context only, not the denominator of this paired comparison.

The paired PROFILE raster p95 improves about 60.6×; all measured frames still exceed 16.67 ms. Cold cache preparation was 11.7 seconds. Host tests overlapped the early profile run, so load was not controlled. Both original and cached passes are in the same process/build; repeated physical-device runs are needed for representative acceptance.

The final debug benchmark rerun improves raster p95 about 17.8× with 13.7 seconds to cache readiness. Host regression tests overlapped part of this run as well. Variation between runs reinforces the need for controlled physical-device acceptance. These measurements precede the final process-wide cache-build gate; that guard is covered by the subsequent full host regression and does not alter the single-page warm painter used here.

RELEASE measurement was attempted with the command below and rejected by the installed tool: “Flutter Driver (non-web) does not support running in release mode.” No release timing is claimed. A native Android instrumentation benchmark harness would be a separate setup; this phase uses the supported PROFILE performance path.

## Dominant cost and changes

FrameTiming isolates raster work as the dominant original dense-page cost (seconds of raster time versus milliseconds of build time). The original painter emits a circle and round line for each vector segment; viewport changes replay that dense display list. This evidence identifies rasterization as the dominant subsystem, not a GPU instruction-level profile.

`CommittedInkCache` builds a disposable derived image using the **unchanged vector painter**. Original immutable strokes, coordinates, pressure and timing samples remain authoritative. No image is serialized or written to disk. Pages below 2,000 points use vectors. Larger pages debounce preparation for 200 ms, cache at up to 4× device resolution subject to an eight-million-pixel cap, and composite one image during supported viewport transforms. Above cached resolution, the painter uses original vectors. Filtering during compositing is a display approximation, never a clinical data transformation. Native-sampling pixel-equivalence and high-zoom vector-fallback tests pass.

Append-only edits reuse the cached prefix and paint the uncached suffix until a replacement is ready. Erase, undo, replacement, size and pixel-ratio changes invalidate incompatible surfaces immediately. One cache build runs at a time; stale asynchronous results are disposed. A clear/undo during preparation cannot schedule a blank or undersized replacement cache. Each page/history view owns and disposes its cache. There is no global patient/page cache. Committed and active RepaintBoundaries remain separate.

One retained surface is at most 32 MiB RGBA; old plus in-flight replacement can transiently reach about 64 MiB for one live renderer, excluding GPU overhead, recorded pictures and vector objects. A process-wide boolean gate permits only one non-cancellable GPU cache build, including a build belonging to a recently disposed view; waiting live views retry after 200 ms and disposed views cancel their timer. The gate holds no clinical data. Thus rapid navigation cannot accumulate one in-flight allocation per departed page. Each additional live renderer can still retain its own surface; this is not a total process-memory bound. Allocation/recording failure falls back to vectors. Cache construction still performs O(points) work and can cause visible stalls; high zoom and cold pages can still hit the original raster cost.

Active input still appends samples without cloning the stroke or notifying the committed layer on moves. Active painting remains O(active points) per frame; summed painting across a growing stroke can therefore be quadratic over the session. No claim of constant-time active painting is made. The stress fixture verifies 20,000 samples without loss and repeated undo/redo/pinch/pan/reset without modifying stored coordinates.

## Storage, navigation and failure handling

MessagePack defensive validation, local JSON validation, isolate-based draft serialization and SQLCipher are unchanged. Timing includes validation; no double-decode check was removed merely to improve a benchmark. The integration harness measures codec and encrypted save/load separately, reopens the dense draft five times, and deletes only its synthetic test database afterward.

Host stress exercises dense A → B → A ownership across six switches, 100 switches across 50 pages, 300 draft open/close operations, 100 queued ACKs, 5,000 progressively loaded revision metadata records, and oversized-draft retention. Inactive controllers are disposed; undo history is not shared. RSS is observational and GC-sensitive; short runs and cache reference disposal are not proof of a leak-free 20-minute session.

Separate Windows host stress run: dense six switches 1,844 ms with RSS `[153088000, 189984768, 196251648, 207413248, 204685312, 223596544]` bytes; 100 small-page switches 615 ms (+4,341,760 bytes); 5,000 history metadata rows 1,373 ms; 300 in-memory draft open/close operations 652 ms; 100 fake-network queue ACKs 1,574 ms. These last two are ownership/ordering tests, not SQLCipher/network throughput claims. Dense-switch RSS rises during this short run (including retained fake-store documents and GC allocations); sustained-memory acceptance remains unproven.

Host codec/recording run: MessagePack encode 947,890 µs, decode 650,718 µs, local JSON encode 287,084 µs, decode 156,069 µs; 20 dense picture recordings 1,717,538 µs; 20,000 raw active-pointer moves 89,242 µs. Pointer-dispatch and picture-recording timings exclude Android rasterization and cannot establish input latency. Host RSS delta in that mixed scenario was +67,391,488 bytes.

Android PROFILE codec/storage run: MessagePack encode 1,646,872 µs, decode 698,208 µs; JSON encode 511,695 µs, decode 478,735 µs; encrypted save 4,333 ms (includes database open/keying), load 535 ms. Five close/reopens restored 1,000 strokes each, RSS bytes `[293994496, 274153472, 287305728, 273620992, 293437440]`. Ten active-picture recordings took 30,750 / 388,714 / 754,539 µs at 1,000 / 10,000 / 20,000 points respectively. The 20,000-point case averages ~75 ms just for recording; responsive sustained writing at that length is not accepted. SQLCipher reported `mlock` errno 12 on this emulator; encryption succeeded, but memory-locking behavior on the target physical device requires validation.

Final Android DEBUG capacity rerun: large encrypted save 3,268 ms, load 838 ms; database 3,780,608 bytes after the dense draft and 7,360,512 bytes after 51 drafts plus 100 queued operations; enqueueing 100 operations took 23,552 ms, process RSS delta +72,822,784 bytes. Reopen and simulated read-only write failure preserved the prior dense draft and all queue entries. This covers write failure, not a physically full flash device. No clinical draft or queue item is evicted to enforce the display-cache budget.

Final Android DEBUG codec/storage benchmark: MessagePack encode/decode 520,800 / 308,678 µs, local JSON encode/decode 252,170 / 309,451 µs, save/load 2,012 / 241 ms; five dense reopens restored all strokes with RSS `[444514304, 439754752, 454983680, 445890560, 453988352]`. Ten active-picture recordings: 29,305 / 71,944 / 117,555 µs for 1,000 / 10,000 / 20,000 points. These vary substantially from the earlier PROFILE run under different host load; they must not be interpreted as a build-mode speed ranking or device latency limit.

Oversized/unsupported ink remains locally retained, with no truncation or false SERVER SYNCED. Arabic/English text explains that sync cannot proceed, asks the user to check local-save status, and offers keeping the draft and starting a new notebook page or contacting support. Existing encrypted-draft, queue, conflict, logout, lifecycle, patient/account isolation and history safeguards remain covered by regression tests.

## Repeatable commands

From repository root:

```powershell
dotnet build backend/Clinic/Clinic.slnx
dotnet test backend/Clinic/Clinic.slnx --no-build
cd apps/doctor_tablet
flutter analyze
flutter test --timeout 60s
flutter test test/production_stress_test.dart --timeout 120s
flutter test integration_test/encrypted_draft_test.dart -d emulator-5554 --timeout 120s
flutter test integration_test/capacity_test.dart -d emulator-5554 --plain-name "SQLCipher synthetic capacity" --timeout 120s
flutter test integration_test/render_acceptance_test.dart -d emulator-5554 --timeout 600s
flutter drive --driver=test_driver/performance_driver.dart --target=integration_test/render_acceptance_test.dart -d emulator-5554 --profile
flutter drive --driver=test_driver/performance_driver.dart --target=integration_test/render_acceptance_test.dart -d emulator-5554 --release
```

`flutter test --profile` is unsupported by this SDK; use `flutter drive`. Benchmark output contains synthetic counts/times/RSS only, not patient identifiers, coordinates, tokens, keys or payload bytes.

## Physical acceptance — PENDING

No physical Android stylus tablet was available. Before rollout, use synthetic patients and record model, Android version, stylus model, display refresh rate, exact app commit and PROFILE build mode. Check normal/rapid handwriting, pressure, dots, palm/finger contacts during pen contact, finger navigation after release, pinch/reset, dense-page switching, 20+ minutes of writing, background/foreground, network loss/reconnect, and restart after LOCAL SAVED. Record observed stalls and frame distributions, not just pass/fail impressions.

Acceptance requires responsive ordinary writing and usable dense navigation on that device, no lost committed samples, exact patient/page/account isolation, honest local/server save states, persisted restart recovery, retained queue/conflict behavior, and no obvious unbounded memory trend across repeated page cycles. No invented device-specific millisecond threshold is imposed. These functional guarantees have automated evidence; perceived responsiveness, palm rejection, pressure reliability, sustained memory behavior and stylus latency remain pending.

Rollout blockers: physical PROFILE acceptance, remaining cold-cache/high-zoom/very-long-active-stroke costs, device memory validation and production Android signing (existing release configuration uses debug signing). No claim of real-device palm rejection, eraser or latency validation is made.

## Verification

- Backend build: succeeded, 0 warnings/errors. Tests: 296 unit + 747 integration = 1,043 passed, none skipped. No main ClinicDb migration was applied.
- Final Flutter analysis: no issues. Full `--timeout 60s` run: 197 passed.
- Explicit `production_stress_test.dart --timeout 120s`: 8 passed (included in full-suite total).
- PROFILE `flutter drive`: both codec/storage/active and paired-render scenarios passed (runner also counts its teardown as a third result).
- Android encrypted-draft suite: 3 passed, including transactional conflict rollback, encrypted queue upgrade/reopen, encrypted bytes, secure-key handling and wrong-key rejection.
- Android capacity test: 1 passed (selected with `--plain-name 'SQLCipher synthetic capacity'`).
- Final Android debug codec/storage/active and paired-render suite: 2 passed. Focused Android total: 6 passing scenarios; PROFILE adds 2 benchmark scenarios in a separate build.
- An initial all-integration run stopped advancing across a session interruption during the legacy dense-render test and was terminated. It is not counted as passing; the focused Android reruns below provide the final results.
- Cache tests cover native-sampling pixel equivalence, prefix append, erase/undo/page invalidation, zoom fallback, disposal during build, repeated disposal, and recording-failure fallback.

## Phase 2D files

- `lib/features/notebook/ink/committed_ink_cache.dart`: bounded disposable display cache.
- `lib/features/notebook/presentation/ink_page.dart`: cached committed layer and vector fallback.
- `lib/features/notebook/presentation/history_panel.dart`: same disposable renderer for read-only history.
- `lib/l10n/app_en.arb`, `app_ar.arb` and three generated localization files: actionable oversized-draft guidance.
- `test/committed_ink_cache_test.dart`: equivalence, invalidation, fallback, failure and disposal.
- `test/production_stress_test.dart`: additional dense page switching.
- `integration_test/render_acceptance_test.dart`, `test_driver/performance_driver.dart`: paired mode-aware measurements and codec/SQLCipher/active-stroke benchmark.
- This report. Paths above are relative to `apps/doctor_tablet` except this report.

Stopped after Phase 2D. Physical release acceptance remains pending; no production rollout approval is implied.
