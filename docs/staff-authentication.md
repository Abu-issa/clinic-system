# Staff authentication setup and operations

## Scope and architecture

The backend uses ASP.NET Core Identity 10.0.11 password hashing, EF user/role/claim/token stores,
authenticator verification and single-use recovery codes. Infrastructure owns Identity and SQL;
Application exposes `IStaffAuthentication`; API owns cookies, CSRF and HTTP contracts.
Staff accounts are separate from Doctor and Patient records. `AssociatedDoctorId` records the
explicit existing doctor selected at provisioning. Explicit persisted scope claims control access;
association alone grants nothing. Medical services and unit-of-work behavior are unchanged.

There is one full application cookie scheme, `ClinicStaff`, and one restricted scheme,
`ClinicStaffIntermediate`. There are no external-login, remembered-device or password-only full
sessions, public registration, patient OTP, UI, or general staff CRUD endpoints.

Roles remain `Doctor`, `Receptionist`, `DoctorAssistant`. Existing authorization is preserved:
Doctor/Receptionist + MFA permits **clinic-wide booking**; booking does not check doctor scope.
Availability, cancellation, rescheduling and schedule management retain their permission and
doctor-scope checks. DoctorAssistant can authenticate and manage their own session, but cannot
use the current appointment/schedule endpoints. A Doctor role never implies all-doctor scope.

## Manual local setup (not executed against ClinicDb by this task)

Run from the repository root. Review the configured `ConnectionStrings:ClinicDb` first, back up
the target database, and review migration `20260914205919_AddStaffIdentity` before applying it.
It creates only the seven Identity tables; it does not recreate or rewrite medical tables.
Downgrading this migration deletes staff identity data and must not be used as a routine recovery step.

```powershell
dotnet build backend/Clinic/Clinic.slnx --nologo -m:1 -p:UseSharedCompilation=false
dotnet ef database update --project backend/Clinic/src/Clinic.Infrastructure --startup-project backend/Clinic/src/Clinic.Api
dotnet run --no-build --project backend/Clinic/src/Clinic.Api -- staff provision --username <staff-name> --doctor <existing-doctor-guid> --scopes <doctor-guid,doctor-guid>
```

The last command runs locally and exits without starting the HTTP listener. It prompts for the
password with echo disabled and refuses redirected stdin. Never pass a password in command-line
arguments, environment variables, `.http` examples, source control, screenshots or shell transcripts.
Use 12–1024 characters meeting Identity's uppercase/lowercase/digit/non-alphanumeric requirements.
The same upper bound applies to provisioning, password reset and HTTP login.
Only an operator with trusted local process/database access should run these commands.

Provisioning creates only the first responsible Doctor, with the four current permissions
(`appointments.availability`, `appointments.cancel`, `appointments.reschedule`, `schedule.manage`)
and both appointment/schedule scopes for exactly the supplied Doctor IDs. The associated doctor
and every scope must already exist; display names are never used to associate accounts.
An identical rerun returns the existing staff ID without changing password, secret, MFA state,
enabled state, grants or security stamp. A different configuration fails. Once an account exists,
this command cannot create another username. Additional staff onboarding is outside this initial command.
No startup credential seed or automatic migration runs.

Start the API using its HTTPS launch profile. Trust the local development certificate through
your normal .NET development setup. Use a cookie-aware HTTPS client for the flow below.

## Browser API contract

All routes are under `/api/staff/auth`; responses are no-store. Send `X-CSRF-TOKEN` and the
antiforgery cookie on every POST, including anonymous login and logout. Cookies are HttpOnly,
Secure, SameSite=Strict, Path=/, with `__Host-` names. Do not copy cookies into localStorage.
Keep the CSRF request token only in client memory. Credentials, setup data and recovery codes must
not be sent to telemetry, request/response body logging, analytics or third-party QR generators.

| Method and route | Input / result |
|---|---|
| `GET /csrf` | Sets antiforgery cookie; returns `requestToken` for the current full, restricted or anonymous identity. |
| `POST /login` | `{ "userName": "...", "password": "..." }`; password verified first. Returns `next: enrollment` or `next: totp`, and refreshed `csrfToken`. |
| `POST /enrollment/setup` | No body. Requires the password-verified enrollment cookie. Returns `sharedKey` and `authenticatorUri` for manual entry/local QR rendering. |
| `POST /enrollment/verify` | `{ "code": "..." }`; enables MFA only on successful Identity authenticator verification; returns full cookie, refreshed `csrfToken` and ten `recoveryCodes` once. |
| `POST /totp` | `{ "code": "..." }`; requires password challenge; returns full cookie and refreshed `csrfToken`. |
| `POST /recovery` | `{ "code": "..." }`; requires password challenge; atomically consumes one recovery code and returns full cookie and refreshed `csrfToken`. |
| `GET /session` | Full MFA session only; returns `staffId` and roles. No password metadata, scopes, keys, codes or authentication tokens. |
| `POST /logout` | Clears both cookies and revokes all sessions for the current account, including a restricted session. Returns anonymous `csrfToken`. |
| `POST /revoke-sessions` | Full MFA session only; revokes all account sessions, clears cookies and returns anonymous `csrfToken`. |

Use each returned `csrfToken` for subsequent POSTs, or fetch `/csrf` again after identity changes.
Enrollment/second-factor sessions never carry roles, permissions or `amr=mfa`, and are never the
default authentication scheme for medical endpoints. Full sessions last 30 minutes; intermediate
sessions last five minutes. Neither slides or remembers a device. A new password challenge replaces
the previous challenge for that account; successful verification consumes it. Login from another
browser does not revoke existing full sessions. Logout deliberately revokes *all* full sessions.

Failures use Problem Details with `code` and `traceId`: `invalid_credentials` (401),
`invalid_csrf_token` (400), `rate_limited` (429); policy denials remain 401/403 API responses, with
no HTML redirects. Invalid DTO/model binding is 400 using the existing ASP.NET validation format.
Roles/permissions/scopes are never accepted from auth DTOs or headers; unknown JSON fields have no effect.

Five failed password, TOTP, enrollment-verification or recovery attempts lock the account for
15 minutes. A correct password alone never resets the failed-attempt counter. Lockout invalidates
pending and full sessions. Only successful MFA resets failures. Identity's authenticator provider
uses server UTC: keep clocks synchronized. An authenticator code can be valid within Identity's
supported time window; the persisted *login challenge* and recovery code are single-use.

All auth routes share a fixed-window limit of 30 requests/minute per hashed source-IP bucket,
with no queue and 256 total buckets to bound memory. Collisions and shared clinic NATs can share
capacity. Limits are local to a process; account lockout and challenge consumption are persisted
and serialized across processes. Before multi-instance/public deployment, add an upstream global
limit and choose capacity for clinic traffic. Forwarded headers are not trusted by this app;
configure only known proxies if deploying behind one. No CORS cross-site credential flow is enabled.

## Revocation and operator procedures

On every authenticated request, both cookie schemes read persisted enabled state, security stamp,
lockout and approved role membership. Intermediate sessions additionally validate challenge ID,
expiry and enrollment state. Revocation is effective on the next request, without the normal
Identity periodic-validation delay. Requests already authorized before a concurrent revocation
may finish. No offline session validation or revocation cache is used.

Use the returned **staff ID**, never the Doctor ID, for these explicitly invoked local operations:

```powershell
dotnet run --no-build --project backend/Clinic/src/Clinic.Api -- staff revoke --user-id <staff-id>
dotnet run --no-build --project backend/Clinic/src/Clinic.Api -- staff disable --user-id <staff-id>
dotnet run --no-build --project backend/Clinic/src/Clinic.Api -- staff reset-password --user-id <staff-id>
dotnet run --no-build --project backend/Clinic/src/Clinic.Api -- staff reset-mfa --user-id <staff-id>
dotnet run --no-build --project backend/Clinic/src/Clinic.Api -- staff set-grants --user-id <staff-id> --roles Doctor --permissions appointments.availability,appointments.cancel,appointments.reschedule,schedule.manage --scopes <doctor-guid,doctor-guid>
```

`set-grants` replaces the complete approved role/permission/scope set. It validates role and
permission allowlists and existing doctor scopes. Every operation updates the security stamp and
clears the pending challenge in the same per-account SQL lock/transaction as credential changes.
Password reset uses Identity's reset-token API internally; no reset token is printed or returned.
MFA reset disables MFA, replaces the secret and removes old recovery codes; it does not issue a
session or return the new secret. Do not mutate user, role or claim tables directly: direct SQL
grant edits do not automatically rotate stamps and are outside the supported administrative path.

For a lost authenticator, use password + an unused recovery code if available. If none remains,
an administrator must verify the staff member through an established independent, in-person
process, record the verification and affected staff ID in the clinic's administrative audit record,
revoke sessions, reset the password if compromise is possible, and invoke `reset-mfa`. The staff
member must then log in with the password and complete restricted enrollment again, storing the
new recovery codes securely offline. A disabled account remains disabled. No remote/anonymous
MFA reset or identity-verification shortcut exists. Operator audit storage/UI and subsequent
account creation are not implemented in this change.

## Secret storage and deployment

Identity hashes passwords. Its default authenticator and recovery-code store is **not application-level
encrypted**: authenticator keys and recovery-code values are stored in `AspNetUserTokens.Value`.
This implementation uses that supported store without claiming those secrets are encrypted.
Before using real accounts, protect SQL storage and backups with encryption, restrict the service
and database operator identities, and approve the remaining risk that a database reader can obtain
MFA secrets. SQL storage encryption does not prevent a privileged SQL reader from reading them.
Application-level envelope encryption would require a custom store/protector and managed key lifecycle;
it is not included here. Back up the SQL encryption certificate/key independently and rehearse
restore; loss of those keys can make the encrypted database/backups unrecoverable.
See the [Identity store implementation](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Identity/Extensions.Stores/src/UserStoreBase.cs).

Cookie and antiforgery protection uses ASP.NET Core Data Protection. Development currently uses
the platform defaults. Before deployment configure a stable, environment-specific application
name, a persistent key ring outside the repository with service-account-only ACLs, and explicit
at-rest key protection (Windows DPAPI for a single machine/service identity, or a managed
certificate/key vault for shared deployments). Configuring a file location alone does not guarantee
encryption. Persist/share the same key ring and discriminator across intended API replicas;
never share it across unrelated applications or environments. Keep retired wrapping keys/certificates
needed for rotation and disaster recovery. Losing this ring invalidates cookies and antiforgery
tokens; it does not erase the SQL-stored authenticator secrets. Do not embed DP keys, certificate
passwords or SQL credentials in appsettings or source control. Follow
[Microsoft's Data Protection deployment guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

Expose HTTPS only at the deployment boundary and configure HSTS there. The API redirects HTTP,
but clients must never send credentials over HTTP first. Review request-body logging, tracing,
proxy capture, backup access and trusted-proxy configuration before handling real credentials.

## Verification

`StaffIdentityHttpTests` uses actual password hashing, provisioning, Identity token verification,
cookie issuance, antiforgery, revocation and SQL stores; its test-only TOTP generator uses synthetic
secrets and UTC. Each test owns an isolated `ClinicTests_*` database. Existing appointment,
schedule and access tests still generate protected tickets through their helpers; those helpers now
create persisted synthetic staff records so the production cookie validator is exercised. No test
replaces the production authorization or cookie-validation handler. The old no-database HTTP
factory now owns an isolated database for those synthetic staff records.

The migration-upgrade test preserves an existing 47-minute unclassified appointment, its times,
row version, patient and doctor while adding Identity. Existing appointment tests retain their
policy, concurrency, CSRF and response assertions. See the current progress checkpoint for final counts.
