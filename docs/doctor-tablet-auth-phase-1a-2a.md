# Doctor Tablet Phase 1A-2A: mobile staff authentication

This document records the original Phase 1A-2A slice. Refresh rotation, the current token response
and session-family lifetime are documented in [Phase 1A-2B](doctor-tablet-auth-phase-1a-2b.md).

This slice adds authentication only. Flutter, notebook permissions/data/APIs, patient context,
drawing, offline storage, registration, refresh tokens and refresh rotation are outside scope.
The existing `ClinicStaff` web cookie scheme remains the default.

## HTTP contract

All responses are `Cache-Control: no-store`. Use HTTPS and JSON request bodies.

| Endpoint | Request | Success |
| --- | --- | --- |
| `POST /api/mobile/staff/auth/login` | `{ "login": "username-or-email", "password": "..." }` | `200 { next: "totp", challenge, expiresIn: 300 }` |
| `POST /api/mobile/staff/auth/mfa` | `{ "challenge": "...", "code": "..." }` | `200 { accessToken, tokenType: "Bearer", expiresIn: 900, expiresAtUtc }` |
| `GET /api/mobile/staff/auth/session` | `Authorization: Bearer <accessToken>` | `200 { staffId, roles }` |
| `POST /api/mobile/staff/auth/logout` | Same Authorization header | `204`; revokes this mobile session only |

Credential failures return generic `401` Problem Details (`invalid_credentials`); missing/invalid
sessions return `401`. The existing bounded `staff-auth` IP limiter returns `429`.
No cookies or CSRF tokens are issued or accepted as mobile authentication. Bearer values belong
in the Authorization header, never query strings. Web cookies alone cannot authorize these
protected endpoints, and mobile tokens cannot authorize existing web endpoints.

## Identity and MFA

Identity password validation, persisted enabled/role checks, the five-attempt/15-minute shared
lockout, TOTP provider, SQL transaction-owned staff locks, challenge consumption, security stamps,
and existing administrative revocation remain authoritative. Doctor, DoctorAssistant and
Receptionist can authenticate. Username/email matching uses Identity normalization; identifiers
matching multiple accounts fail closed because the existing email index is non-unique.

Existing policy requires MFA for every full staff session. Accounts without enrolled MFA receive
`403 mfa_enrollment_required` after valid password verification and must enroll through the
existing staff web flow. Mobile does not expose enrollment or recovery-code endpoints.
The protected, purpose-isolated MFA challenge expires after five minutes. The existing persisted
challenge also enforces expiry and single use. A newer password login (web or mobile) supersedes
the previous pending challenge. Password success alone never resets failed attempts.

## Session and revocation

Access credentials are opaque base64url values containing 32 cryptographically random bytes.
Only their SHA-256 digest is persisted in `MobileStaffSessions`; raw bearer tokens are returned
once. The persisted principal is Data Protection encrypted and contains staff ID, security stamp,
roles, MFA status and session ID, with no clinical permissions or scopes. Challenges and sessions
use distinct Data Protection purposes from each other and from web cookies.

Sessions expire absolutely after 15 minutes with no sliding renewal or refresh. Each request
checks the persisted session expiry/revocation and current Identity enabled state, lockout,
MFA status, security stamp and roles. Existing account-wide revoke, password/MFA resets and web
logout invalidate mobile sessions through the stamp. Account revoke permits a fresh login;
disabling the account blocks new login too. Mobile logout affects only its current session.
Revocation applies to subsequent authentication checks; it cannot cancel a request already accepted.

Audit events use the existing best-effort security audit writer and staff-account resource:
`staff.mobile.password.accepted`, `staff.mobile.session.created`, `staff.mobile.session.revoked`.
They contain no credential, challenge or token metadata. Audit persistence failures retain the
existing sanitized operational error signal and do not block authentication.

## Database and operations

`AddMobileStaffSessions` adds one table, a staff-user foreign key, a unique token digest index,
and expiry/user indexes. Migration and snapshot were generated only; do not apply to main ClinicDb
as part of this slice. Integration tests migrate only generated `ClinicTests_*` databases.

Production requires HTTPS, protected durable Data Protection keys shared by API instances, and
proxy/logging configuration that never records Authorization headers or credential request/response
bodies. Losing keys invalidates challenges and mobile sessions. The inherited rate limiter is
per-process; cross-instance throttling is outside this slice. Expired/revoked session cleanup,
device binding, session inventory, refresh rotation and mobile secure token storage are deferred.
Bearer tokens can be replayed if stolen until expiry or revocation.

Focused SQL-backed HTTP tests are in `StaffIdentityMobileHttpTests.cs`, using the existing
Identity test fixture and real web enrollment/TOTP. Run them with:

```powershell
dotnet test backend/Clinic/tests/Clinic.IntegrationTests/Clinic.IntegrationTests.csproj --filter FullyQualifiedName~StaffIdentityHttpTests.Mobile
```

## Changed files and verification

Added:

- `backend/Clinic/src/Clinic.Api/Authentication/MobileStaffHandler.cs`
- `backend/Clinic/src/Clinic.Api/Controllers/MobileStaffAuthenticationController.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Authentication/MobileStaffAuthentication.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Authentication/MobileStaffSession.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260919002238_AddMobileStaffSessions.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260919002238_AddMobileStaffSessions.Designer.cs`
- `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffIdentityMobileHttpTests.cs`
- `docs/doctor-tablet-auth-phase-1a-2a.md`

Updated:

- `backend/Clinic/src/Clinic.Api/Program.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Authentication/StaffAuthentication.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Authentication/StaffIdentityRegistration.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/ClinicDbContext.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/ClinicDbContextModelSnapshot.cs`

Verification completed:

- `dotnet build backend/Clinic/Clinic.slnx`: succeeded, zero warnings/errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build`: 269 unit and 645 integration tests passed,
  zero failures/skips, including all 15 new mobile authentication cases.
- EF `migrations has-pending-model-changes`: no pending model changes.
- `git diff --check`: passed.

Tests cover valid username/email and all three staff roles, missing MFA enrollment, invalid
password/TOTP and shared lockout, successful MFA, single-use/expired/superseded challenges,
disabled accounts and administrative revocation/reset, current-session logout, absolute expiry,
scheme separation, forged challenges, role removal without stamp rotation, ambiguous email,
hashed bearer persistence and secret-free logs/security audit metadata.
