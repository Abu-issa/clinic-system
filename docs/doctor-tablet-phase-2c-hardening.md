# Doctor Tablet Phase 2C — Hardening and device readiness

Phase scope: repeatable synthetic stress tests, safe rendering optimization,
oversized-document failure UX, debug-only stylus instrumentation, and a readiness
review. No new clinical module, backend redesign, migration, export, OCR, merge,
background service, or native bridge. Main ClinicDb was not migrated. Backend
integration tests use the existing generated `ClinicTests_*` fixture databases.

## Repeatable runs

From `apps/doctor_tablet`:

```powershell
flutter test test/production_stress_test.dart --timeout 60s
flutter test --timeout 60s
flutter analyze
flutter test integration_test -d emulator-5554 --timeout 60s
```

`test/stress_fixture.dart` generates synthetic vectors only. Test output includes
numeric `STRESS`, `FRAMES`, and `CAPACITY` records; it prints no ink, identifiers,
credentials, keys, or clinical content. There are no timing pass/fail thresholds:
these measurements are observations, not a clinical responsiveness SLA. Compare
repeat runs on the same device/build while controlling host load.

The tests cover 1,000 strokes / 50,000 points; a 20,000-point active stroke with
zero committed-layer notifications during movement; 500 undo/redo pairs; 1,000
pinch/pan/reset cycles; 50 notebook pages traversed twice; 100 drafts opened and
closed three times; 100 queued revisions drained in bounded batches; 5,000 history
records across 250 metadata-only requests; encrypted large writes and reopen;
and save/upload failure recovery.

## Measurements

Host measurements below are from the full debug Flutter test run on Windows on
2026-09-27, concurrently with other verification. They use fake HTTP and an
in-memory draft fixture except for codec/painting work. They are not production
network or SQLCipher throughput. Timings varied between runs under host load.

| Scenario | Observed result |
|---|---:|
| MessagePack v2, 1,000 strokes / 50,000 points | 1,590,742 bytes |
| MessagePack encode including validation | 798.624 ms |
| MessagePack decode | 459.985 ms |
| Local JSON draft encode / decode | 329.713 / 504.171 ms |
| Record 20 committed-page display lists | 1,594.512 ms total |
| Dispatch 19,999 moves after down (20,000-point stroke) | 47.059 ms total |
| 100 actual notebook page switches, fake transport/store | 642 ms |
| 300 local handle open/save/release operations | 480 ms |
| 100 queue ACKs, fake HTTP/store | 834 ms |
| 5,000 historical metadata entries | 1,249 ms |
| RSS change during codec/render/active-stroke scenario | +34,553,856 bytes |
| RSS change during 100 notebook page switches | +16,359,424 bytes |

The earlier isolated host run measured encode/decode at 451.106 / 368.403 ms and
20 display-list recordings at 947.246 ms. This variation is why no speedup or
latency guarantee is claimed. Display-list recording excludes GPU raster work;
event dispatch excludes painting, platform sampling, and input-to-display latency.
RSS includes allocator/JIT/cache behavior and is not a retained-heap leak test.

Android integration device: **Pixel Tablet AVD (emulator), Android 15 / API 35**,
debug build, not a physical tablet. Run finished and was reviewed on 2026-09-28.

| Native scenario | Observed result |
|---|---:|
| 60 scale/rebuild iterations, 50,000-point page | 118 FrameTiming samples |
| Raster p50 / p95 | 1,693.800 / 2,196.549 ms |
| Build p95 | 31.756 ms |
| SQLCipher large draft save / read | 1,906 / 623 ms |
| DB after one 50,000-point draft | 3,780,608 bytes |
| DB after 51 drafts + 100 queue entries | 7,360,512 bytes |
| Persist 100 queue entries | 16,756 ms total |
| RSS change during native capacity scenario | +18,919,424 bytes |

Native queue entries contain 10-stroke / 500-point payloads; the primary draft
contains 50,000 points. DB figures are measured main-file lengths, not a forecast
for patient populations or an accounting of every temporary/journal file.

**Readiness concern:** the dense synthetic page performs very poorly in debug
emulator rasterization. The frame scenario took about 3m26s; passing the integration
test is not a performance pass. Real-device profile/release measurements and a
clinically representative long-writing workload are required before deployment.
The synthetic strokes overlap heavily; results must not be extrapolated to every
real page, nor dismissed without investigation.

## Rendering review and changes

- Committed and active CustomPainter layers and RepaintBoundaries remain separate.
  Pointer movement appends one point to the active buffer; it does not copy the
  committed stroke collection or notify its painter. Stroke copies occur on commit.
- Immutable `InkStroke.usesPressure` is now calculated lazily once. Previously it
  rescanned every stroke's points on committed repaint, including viewport changes.
  Cached derived data does not change vector coordinates, pressure, or output.
- Active rendering still traverses the active stroke on paint. A long active stroke
  and dense committed-page rasterization remain bottlenecks. MessagePack encode
  also performs a defensive decode validation, adding allocations and CPU time.
  These were not replaced with risky geometry batching, simplification, or raster
  source data in this phase.
- Undo is already capped at 100 snapshots referencing immutable stroke objects.
  Navigation tests verify inactive handles dispose, undo state is released, and
  only the selected controller accepts input. History holds one payload, and
  leaving history releases it. Status/list metadata is retained, not all ink.

## Storage, lifecycle, and failure UX

- Native tests reopen the encrypted DB after the large write/backlog and verify
  all 51 page metadata entries and 100 queue operations survive with owner scope.
  Existing native tests continue checking encryption, wrong-key rejection,
  upgrade/order, and atomic conflict rollback.
- A deterministic read-only SQLCipher write rejection leaves the prior large draft
  and queue intact. Fixture-based write failures overlap `flush` and background
  `flushAll`, retain unsaved ink, refuse logout, and succeed on retry/reopen.
  Actual disk exhaustion, power loss, and filesystem corruption were **not** induced.
- Additional coverage pauses an in-flight upload, preserves its exact identity
  and bytes, and resumes with one acknowledged server mutation. Existing tests
  cover partial-stroke app background flush, network timeout/uncertain ACK retry,
  session/account switch isolation, queued restart, and unresolved-conflict reopen.
  Reopen tests reconstruct stores/managers; they do not prove abrupt OS-kill recovery
  for an edit that was never successfully persisted.
- Oversized/invalid transport ink now receives a localized actionable message:
  it cannot sync, no ink was removed, check local save status and contact support.
  The handler attempts encrypted local flush and does not truncate or enqueue it.
  A regression test retains all 20,001 points in an over-limit stroke.
- Existing local-save failure/retry, unreadable draft/retry, safe sync states,
  explicit conflict choices, and blocked-unsaved-logout flows remain. Raw exception
  messages and server bodies are not shown by these flows. Arabic and English
  messages remain localized. No arbitrary capacity cutoff or deletion policy was
  added from this small benchmark; existing server limits still apply (4 MiB,
  4,096 strokes, 100,000 total points, 20,000 points per stroke).
- Limitations needing operational handling: an oversized saved draft has no export
  escape hatch in this MVP; storage exhaustion may leave work only in memory;
  session expiry retains drafts but requires reauthentication. Stopped permission,
  lifecycle, and corrupt-data sync failures intentionally do not auto-retry.

## Development-only diagnostics

In a debug build, the authenticated toolbar has a bug icon opening a standalone
synthetic test surface. Both the entry and screen are guarded with `kDebugMode`;
release/profile builds expose neither the button nor screen content/callbacks.
No release artifact inspection or physical-device execution was performed.

The screen shows pointer kind (including Flutter's `invertedStylus`), raw pressure
and min/max, average event rate since opening, last pen down/up/cancel, touch-down
count during a pen contact, frame count, mean build/raster time and max raster
time. It refreshes four times per second rather than rebuilding per event.

Only aggregate numbers and transient contact IDs are retained. There are no
patient/session/draft dependencies, coordinate fields, event buffers, persistent
logs, or network calls. Timer and frame callback are removed on disposal. This
surface observes signals; it does not validate palm rejection or map hardware
eraser signals to clinical editing.

## Security review

Source review of tablet production code found:
- Tokens use `SecureTokenStore` / FlutterSecureStorage, with redacted token
  `toString`; no SharedPreferences token or ink writes.
- Drafts/queue use mandatory SQLCipher and a secure-storage key; lost keys do not
  trigger replacement/deletion. Database and secure preferences are excluded
  from Android cloud backup and device transfer by existing rules.
- No `print`, `debugPrint`, HTTP logging interceptor, plaintext file-write path,
  certificate-accept callback, or HttpOverrides in production tablet code.
- Production ApiConfig requires HTTPS; Dio disables redirects and preserves
  ordinary platform TLS validation.
- Existing owner/server/patient/page validation and late-response tests pass.
  Diagnostics do not print or store clinical content, tokens, or database keys.

This is a scoped source/test review, not penetration testing, dependency auditing,
binary reverse-engineering, or a guarantee about every platform/plugin log.

## Physical device checklist — ALL PENDING

No physical Android stylus tablet was available. Record model, Android/build
version, pen model, app commit/build mode, refresh rate, network conditions, and
test duration before running. Use synthetic patients and nonclinical handwriting.

- [ ] Normal handwriting; fast handwriting; dots and short strokes.
- [ ] Pressure variation and min/max reliability in diagnostics; fixed-width fallback.
- [ ] Pen near/on screen with palm and finger contacts; record actual device behavior.
- [ ] Finger navigation resumes after pen release; two-finger pinch and one-finger pan.
- [ ] Toolbar whole-stroke eraser; inverted stylus/hardware eraser signal detection
      if exposed (no hardware eraser editing feature is promised).
- [ ] Long writing session with dense pages; profile build frame statistics,
      responsiveness, memory plateau, thermal behavior, battery use.
- [ ] Background/foreground during a stroke, local save, and upload.
- [ ] Network disconnect/reconnect and uncertain upload response; no duplicate ACK.
- [ ] App termination after LOCAL SAVED; reopen drafts, queue, and conflict.
- [ ] Account expiry/re-login and another account/patient: no prior-owner exposure.
- [ ] Low-storage conditions on a controlled test device: preserve unsaved state,
      clear retry messaging, no deletion or false LOCAL SAVED/SERVER SYNCED state.

Real-device pressure, palm rejection, hardware eraser, or latency validation is
not claimed. Remaining MVP rollout blockers are the pending physical checklist,
profile/release dense-page performance investigation, long-session retained-heap
measurement, true low-storage/crash testing, and an operational plan for locally
saved documents that exceed the sync contract.

## Verification and changed files

Backend build: 0 warnings/errors. Full backend tests: **296 unit + 747 integration**,
0 failed/skipped. No Phase 2C backend source changes.

Final verification on 2026-09-28:
- `flutter analyze`: no issues found.
- `flutter test --timeout 60s`: **189 passed**, including 7 new synthetic stress/
  diagnostics tests and the new in-flight upload pause/resume test.
- `flutter test integration_test -d emulator-5554 --timeout 60s`: **5 passed**,
  including 2 new capacity/frame scenarios and 3 existing SQLCipher tests.
- `git diff --check`: no whitespace errors.

The final host regression rerun observed MessagePack encode/decode at
662.942 / 456.728 ms, local encode/decode at 296.164 / 313.121 ms,
20 display-list recordings at 1,449.097 ms, 20,000-point dispatch at 49.739 ms,
300 local handle operations at 505 ms, and 100 fake-network ACKs at 688 ms.
All measurements remain debug synthetic observations, not release acceptance.

Phase 2C files:
- `apps/doctor_tablet/lib/features/notebook/ink/ink_document.dart`
- `apps/doctor_tablet/lib/features/notebook/presentation/stylus_diagnostics.dart`
- `apps/doctor_tablet/lib/features/home/presentation/authenticated_shell.dart`
- `apps/doctor_tablet/lib/features/notebook/state/notebook_cubit.dart`
- `apps/doctor_tablet/lib/features/notebook/presentation/notebook_section.dart`
- Arabic/English ARBs and three generated localization Dart files.
- `apps/doctor_tablet/test/stress_fixture.dart`
- `apps/doctor_tablet/test/production_stress_test.dart`
- `apps/doctor_tablet/test/sync_queue_test.dart`
- `apps/doctor_tablet/integration_test/capacity_test.dart`
- This report. Existing Phase 2B workspace changes were preserved.

Stopped after Phase 2C.
