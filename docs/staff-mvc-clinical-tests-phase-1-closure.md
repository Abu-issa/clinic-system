# Staff MVC clinical tests Phase 1 — final closure review

Review completed on 2026-09-21. Scope is the file-first staff clinical-test workflow only.
**READY TO COMPLETE STAFF MVC CLINICAL TESTS PHASE 1.**
The starting reported baseline was 889 tests (269 unit, 620 integration), including 42 MVC cases.
The shared repository advanced during the interrupted review; unrelated newer features/commits
were preserved and are not work performed or reviewed by this closure.

## Findings and fixes

| Severity | Finding | Resolution |
| --- | --- | --- |
| Critical | None identified | — |
| High | MVC form value providers could replace route patient/request IDs. A user with both scopes could mutate a different patient/request than the URL indicated. Authorization checked the substituted IDs, so this was a context-integrity flaw, not proof of access outside granted scopes. | Explicit `FromRoute` on every MVC patient/request parameter; form-only command binding. Three reproductions failed before the fix and pass afterward; multipart tampering is covered too. |
| Medium | Create had no existing-entity RowVersion and could create duplicate orders when the same form was posted twice. | Bounded, atomic, single-use create-form token tied to actor and patient. Concurrent same-form POSTs produce one create/audit and one safe 409. Application validation remains authoritative. |
| Low | Existing download errors could present raw API ProblemDetails to a staff browser. | The same protected download endpoint supports an opt-in staff HTML error presentation. No second download implementation, new public path or changed authorization/storage/audit logic. Normal API response behavior remains supported. |
| Low | Disabled upload had no explanation when JavaScript was enabled but failed to load. | Explanation is visible in initial HTML and hidden only after the upload handler initializes. No pre-body-CSRF bypass or unsafe fallback. |
| Low | Language navigation label and the no-visits state needed localization. History-restored forms could retain old state/disabled controls. | Bilingual labels/empty state, submission guard in JS, and GET reload for browser back-forward cache restores. |
| Informational | Create-form tokens are process-local and expire after 20 minutes; capacity is bounded to 10,000. | Restart, eviction, another instance or replay fails closed. Deploy with session affinity for a usable multi-instance experience; a shared atomic store would be needed for seamless non-sticky deployment. This is not durable idempotency or global deduplication of separately opened forms. |
| Informational | Visual inspection and live HTTP smoke are distinct from browser E2E. | Explicit evidence below; no browser login/upload E2E claim. |

No unresolved critical/high/medium finding remains in this slice.

## Review conclusions

| Area | Evidence / conclusion |
| --- | --- |
| Every route | List, create GET/POST, detail, upload and both review POSTs independently reject missing session/MFA/role/scope/permission. Existing cookie revalidation is retained; Receptionist denied. Doctor authority is re-read by services, including review after association removal. |
| Cross-patient | Patient A + request/visit/attachment B returns hidden-resource responses, contains no foreign filenames, MIME/test values or foreign IDs, and creates no successful patient-A audit event. |
| Attachment visibility | Test-only HTML omits attachment ID, filename, MIME, size and download URL, including links/attributes/hidden inputs. Count remains intentionally visible. |
| Concurrency | Upload/start/complete use the displayed RowVersion. Duplicate/stale submissions leave versions, files and successful mutation audit counts unchanged; redirected detail renders the latest token. No automatic POST retry. |
| Double submit | A create-form token is consumed atomically before application creation. Missing/foreign/replayed tokens fail closed. Existing RowVersion/state-machine protection handles upload/review. JS improves UX but is not the only enforcement. |
| PRG / TempData | Successful mutations redirect to GET; a refresh adds only a page read. Decrypted test TempData contains only the `Notice=Saved` catalog key; all production assignments are fixed catalog keys. No clinical text/filenames are stored there. |
| CSRF | Existing missing-token tests plus invalid-token cases cover every POST. Upload meter verifies zero body reads on rejection; no result row/object/successful upload audit. |
| JavaScript | No innerHTML/document.write/raw-response insertion. FormData carries the token header; manual redirects prevent duplicate audited GETs. No-JS/script-failure upload remains unavailable with explanation. |
| XSS | Synthetic malicious patient/test/instruction/filename values are encoded by Razor, including filename attributes. Query notice injection does not enter notices. The custom error renderer HTML-encodes its catalog output. No Html.Raw exists in these views. |
| Localization / bidi | Arabic default, English equivalents, localized status text. BDI/dir=auto protect MRN/reference, filenames, MIME, dates, sizes and clinical prose. Native file chooser follows OS/browser language. |
| Headers | Staff pages and staff-mode download errors carry no-store, no-referrer, nosniff and restrictive self-only script/style CSP with frame/object blocking. No unsafe-inline/eval or CDN fallback added. |
| Assets | Local Bootstrap 5.3.8 LTR/RTL CSS and MIT license present. Both Production integration hosts and the Development HTTPS host serve the assets. No conflicting Bootstrap version introduced. |
| Patient header | Minimal name + MRN/reference is queried from the authorized route patient; no phone/DOB/diagnosis/address/staff-account fields are projected. |
| Filtering / paging | Existing bounds (pages 1–1000, size 1–100), enum validation and patient predicate retained. Tests exercise category/status paging links, HTML-encoded query separators and stable requested-time/ID order. |
| Overposting | Route IDs cannot be replaced. Only category/name/visit/instructions and the form guard enter create input; doctor/status/times/storage/permission/role fields remain server-controlled. |
| Visit picker | At most 100 current-patient/live-doctor visits; no clinical notes. Posted VisitId independently revalidated. No choices produces a clear standalone-request explanation. |
| Upload path | Shared resource gate, one-file parser, attachment file policy, streamed feature limits, private storage, RowVersion and compensation retained. MVC does not buffer full file byte arrays or implement a weaker validator. |
| Review state UI | Requested: no review; Uploaded: Start; UnderReview: Complete; Reviewed: no mutation controls. Actual state machine remains in Application/domain. |
| Downloads | Existing attachment route only; attachment disposition, nosniff, no-store, hidden-resource semantics and fail-closed file.download retained. Missing object gives safe localized 503 in staff mode. |
| Audit counts | Exactly one create, one file.upload + result.upload pair, one start and one complete per successful command. Page GETs have one appropriate read event. No MVC mutation event construction. |
| Fail-closed reads | Existing list/create-context/detail failure injection prevents protected content before Razor rendering. Download audit failure prevents file bytes and filename disclosure. |
| Errors | Safe localized HTML/notices cover 400/401/403/404/409/413/415/500/503. No SQL, physical paths, keys, exception messages or raw ProblemDetails in staff UI flow. |
| Accessibility | Labels, semantic headings/table headers, textual statuses, focus styles, alerts and skip link reviewed. Browser Tab reaches the skip link. Native controls retain keyboard access. This is not a formal accessibility certification. |
| Empty states | No tests/files/visits, missing attachments.read, Reviewed, failed upload, stale token, forbidden and storage failure all have explicit text. |
| Boundaries | MVC orchestrates existing services. The new submission token is transport-level replay protection, not a clinical lifecycle/authority/storage/audit implementation. |

## Development smoke and responsive evidence

`MvcHardeningDevelopmentHttpsSmokeUsesSyntheticDatabaseAndRealAssets` starts the actual Program
host in Development using Kestrel over a dynamically selected loopback HTTPS port. It uses the
existing trusted development certificate (no validation bypass), persisted fixture staff cookies,
synthetic data in a disposable `ClinicTests_*` database and disposable private storage. It verifies
Arabic list/detail, English create, form CSRF/submission tokens, upload controls, all four review
states, local JavaScript and both Bootstrap CSS variants. Host/database/storage are disposed by
the test fixture. No migration was applied to ClinicDb or another non-test database.

The host's rendered HTML was exported with form tokens redacted, then inspected in the browser:

- Laptop: 1366 × 768, Arabic detail with long mixed Arabic/English test name.
- Narrow desktop: 800 × 900, attachment filename wrapping, MIME/date/size, upload/review controls.
- Tablet landscape: 1024 × 768, English create form and Arabic history/filters.

No page-level horizontal clipping or unusable controls were observed. Long filenames wrap;
table wrappers retain safe horizontal scrolling when needed. Temporary viewport override and
preview server were removed. These are browser snapshot checks plus a live HTTPS integration
smoke test, **not an authenticated browser end-to-end create/upload flow**.

## Regression tests and final commands

MVC-focused suite: **76 passed**, up from 42 (34 added hardening cases). The new test file covers
route/field tampering, every-route access rejection, foreign resources, invalid CSRF, concurrent
create and duplicate lifecycle POSTs, refresh safety, encoded malicious values, metadata hiding,
deterministic pagination, staff download errors, live doctor changes, token ownership, safe
TempData/empty states and the Development HTTPS smoke.

- `git diff --check`: passed (Git line-ending notices only).
- `node --check backend/Clinic/src/Clinic.Api/wwwroot/staff-assets/tests.js`: passed.
- `dotnet build backend/Clinic/Clinic.slnx --nologo -m:1 -p:UseSharedCompilation=false`:
  passed, zero warnings and zero errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build --no-restore --nologo -m:1 --verbosity minimal`:
  **1,023 passed: 281 unit + 742 integration; zero failed, zero skipped**. Integration duration
  was 5 minutes 46 seconds. This is the current shared-checkout total, including unrelated tests
  added independently since the original 889-test baseline; 34 added MVC cases belong to this review.

Review changes span StaffTestsMvcController, StaffAttachmentsController's error presentation,
Program's registration/staff error boundary, StaffTestModels, StaffText, StaffUiErrors,
StaffCreateSubmissions, shared/create/detail views, tests.js, MVC integration tests and these docs.
Some early fixes are already in the shared repository's intervening commits; this agent did not
commit or push. No schema change, non-test migration, new feature or tablet implementation was
performed as part of this closure.

Next: **DOCTOR TABLET + STYLUS WORKFLOW — PHASE 0: HARDWARE / INK
ARCHITECTURE PLANNING**. This is the requested next-step label only; no tablet work is authorized
or started by this review.
