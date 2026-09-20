# Doctor Tablet Phase 1A-3A: patient search and minimal context

## Contract

`POST /api/mobile/staff/patients/search`

Requires `Authorization: Bearer <mobile access token>` and `Content-Type: application/json`.
This read-only search uses a JSON body to keep patient identifiers out of URL/access logs:

```json
{ "searchTerm": "MRN-42", "page": 1, "pageSize": 10 }
```

`searchTerm` is required, trimmed, 2–100 characters, with control characters rejected.
`page` defaults to 1 and accepts 1–100; `pageSize` defaults to 10 and accepts 1–20.
The request size limit is 4096 bytes. Empty search never lists all patients.
Search matches a literal substring of existing full name, MRN or legacy paper file number.
SQL parameters and EF literal substring translation prevent SQL/LIKE-pattern injection.
Case/accent matching follows the existing SQL Server collation; no new normalization is invented.

Example response (HTTP 200):

```json
{
  "items": [{
    "patientId": "00000000-0000-0000-0000-000000000001",
    "fullName": "Example Patient",
    "medicalRecordNumber": "MRN-42",
    "dateOfBirth": "1990-02-03",
    "allergyStatus": 0
  }],
  "page": 1,
  "pageSize": 10,
  "hasMore": false
}
```

MRN and date of birth can be null for existing legacy records. DOB follows the existing DateOnly
convention; derived age is not introduced. AllergyStatus uses the existing numeric enum:
0 = Unknown, 1 = NoKnownAllergies, 2 = HasKnownAllergies. A missing profile returns Unknown.
This is the recorded aggregate status, not a severity or verification claim. No substance,
reaction, medication, diagnosis, contact, legacy cover reference or clinical narrative is returned.
The legacy paper number is searchable but is not included in the projection.

Pagination orders by full name then unique patient ID. Scope filtering happens in SQL before
pagination. One extra projected row determines `hasMore`; no unrestricted totals are calculated.
Out-of-range pages within the allowed bound return an empty page. HTTP 400 indicates invalid
search; 401 indicates missing/invalid mobile authentication; 403 indicates no authorized scope.
All mobile staff responses, including authorization failures, use `Cache-Control: no-store`.

## Reuse and authorization

Adds a search operation to existing `PatientRecordsService`, `IPatientRecordsStore` and
`PatientRecordsStore`; no patient business rules or clinical mutation behavior are duplicated.
The store projects directly from Patient and PatientMedicalProfile without loading medical
entry collections. No existing patient search operation was present.

The endpoint requires the existing `MobileStaffSession` bearer policy. Web cookies cannot
substitute for mobile authentication. `StaffAuthentication.PatientReadPrincipalAsync` clones
the validated mobile identity and loads only current persisted `patients.clinical.read` and
`patient_record_id` grants for its server-derived staff ID. These grants are neither added to
tokens nor to web cookies. Existing `PatientClinicalRead` is evaluated for each candidate scope.
Only approved IDs reach the scoped search query.

Doctor and DoctorAssistant require the same clinical read permission and exact patient scope
as the existing medical-profile read API. This deliberately provides a minimal clinical context
under clinical-read authority; it does not grant the separate full administrative-record API.
Receptionist is rejected even with a clinical permission/scope stored on the account. Body IDs,
doctor/staff headers and forged roles cannot expand access. Patients outside scope are absent,
including from `hasMore`. Direct permission/scope removal is observed on the next search even
without security-stamp rotation. Existing account/role/stamp/MFA/session revocation checks remain.

Each returned row is audited with existing fail-closed `HttpAccessAudit.RecordAsync`:
`patient.context.read`, resource `patient`, patient ID and authenticated staff actor, no metadata.
Search terms and clinical values are not audited. No read event is created for an empty result,
denied search, validation failure, or the extra lookahead row. If any audit write fails, no page
is returned; earlier successful standalone audit events may remain, matching existing conventions.

## Files changed

- `backend/Clinic/src/Clinic.Api/Controllers/MobilePatientContextController.cs` (added)
- `backend/Clinic/src/Clinic.Api/Program.cs` (mobile no-store path coverage)
- `backend/Clinic/src/Clinic.Application/Patients/PatientContextSearch.cs` (added contracts)
- `backend/Clinic/src/Clinic.Application/Patients/PatientRecordsService.cs`
- `backend/Clinic/src/Clinic.Application/Patients/IPatientRecordsStore.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Repositories/PatientRecordsStore.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Authentication/StaffAuthentication.cs`
- `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffIdentityMobilePatientContextTests.cs` (added)
- `docs/doctor-tablet-patient-context-phase-1a-3a.md` (this report)

## Boundaries

No schema changes or migrations, no main ClinicDb migration, no Flutter changes, and no notebook,
stylus or drawing work. Existing working-tree changes from prior slices are preserved.
Tests use real mobile password/TOTP sessions and generated disposable `ClinicTests_*` databases.
The running workspace Clinic.Api process was stopped to release locked build DLLs; it was not
restarted. Authorization is checked per request; in-flight requests cannot be retroactively
canceled. Offset pagination is not a snapshot across concurrent patient updates. Substring
search uses existing indexes/collation and may need future performance work at larger scale.
Infrastructure must continue to exclude patient request/response bodies from logging.

## Verification results

- `dotnet build backend/Clinic/Clinic.slnx`: passed with zero warnings/errors after stopping
  the workspace API process that locked the output DLLs.
- Focused mobile patient-context integration tests: 19 passed.
- `dotnet test backend/Clinic/Clinic.slnx --no-build`: 269 unit tests and 679 integration tests
  passed, zero failures or skips.
- `git diff --check`: passed.

New tests cover Doctor/DoctorAssistant clinical-policy access, name/MRN/legacy search, bounded
pagination, literal SQL-pattern inputs, patient isolation, forged headers/body IDs, missing
permission/scope, Receptionist denial, cookie/anonymous rejection, direct grant removal without
stamp rotation, projection field allowlisting, missing-profile Unknown status, per-returned-patient
audit records, empty/invalid/denied searches without read audit, secret-free search logging, and
fail-closed audit outage behavior.
