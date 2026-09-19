# Doctor Tablet Phase 1A-2B: refresh rotation and replay protection

Extends the Phase 1A-2A mobile session model. ASP.NET Core Identity, the existing staff SQL
application lock, mobile bearer scheme and security auditing remain authoritative. The web
`ClinicStaff` cookie scheme and its behavior are unchanged. No Flutter changes are included.

## Contract

`POST /api/mobile/staff/auth/refresh`, `Content-Type: application/json`:

```json
{ "refreshToken": "<opaque refresh token>" }
```

No access header or cookie is needed; an expired access token does not prevent refresh. Tokens
must never be sent in query strings. The endpoint binds only the JSON body. A successful refresh
returns HTTP 200 with the same token envelope now returned by successful mobile MFA:

```json
{
  "accessToken": "<new opaque access token>",
  "tokenType": "Bearer",
  "expiresIn": 900,
  "expiresAtUtc": "<server UTC access expiry>",
  "refreshToken": "<new opaque refresh token>",
  "refreshExpiresAtUtc": "<server UTC absolute family expiry>"
}
```

Access lifetime is at most 15 minutes. `expiresIn` is the remaining seconds rounded up and may
be less than 900 near the absolute family deadline; UTC expiry fields are authoritative.
The server fixes refresh expiry to seven days after successful MFA and never extends it.
Both access and refresh stop at that deadline. Client-supplied expiry, staff IDs, roles or
session IDs do not control issuance. A new full password/MFA login is needed afterward.

Refresh failures use generic HTTP 401 Problem Details with code `invalid_credentials` for
unknown, expired, revoked, consumed, or Identity-invalid credentials. Malformed/missing request
fields produce standard HTTP 400 validation errors without echoing token values. The existing
`staff-auth` limiter applies (HTTP 429), and all responses have `Cache-Control: no-store`.
No cookies are issued. Refresh tokens cannot authorize protected endpoints; access tokens
cannot be used to refresh.

## Storage and rotation

The existing `MobileStaffSessions` row is the token family. Its nullable `RefreshExpiresAtUtc`
stores the absolute deadline. Legacy Phase 1A-2A sessions retain their access-only behavior;
the migration assigns no refresh capability to existing rows.

Each refresh credential contains 32 bytes from `RandomNumberGenerator`, encoded as base64url.
Only a SHA-256 digest is persisted. `MobileStaffRefreshTokens` holds the digest primary key,
session foreign key, creation time and optional consumption time. Consumed digests are retained
so reuse of any ancestor can be detected. A filtered unique index allows at most one unconsumed
refresh digest per family. Access-token storage remains a SHA-256 digest in the session row.
Neither raw refresh values nor raw access values are stored, even encrypted.

One SQL transaction consumes the old refresh digest, replaces the access digest/expiry, inserts
the new refresh digest and commits. A successful rotation immediately invalidates the previous
access token. Failure rolls back all these changes. The session's protected original principal
is retained: refresh does not acquire newly granted roles, clinical permissions or scopes.

Every refresh revalidates current Identity state through existing `StaffAuthentication.ValidateAsync`:
enabled account, lockout, security stamp, enabled MFA, approved staff role and all originally
issued roles still present. Account revocation, password/MFA reset and role removal invalidate
refresh. Role additions require a fresh login to appear in the principal. Current-session logout
sets the same persisted revocation field checked by both access and refresh.

## Replay and concurrency

The existing transaction-owned `Clinic.Staff:<id>` SQL application lock serializes refresh,
mobile logout and supported Identity administration across API instances. The initial digest
lookup identifies the lock owner only; session and refresh state are re-read after acquiring
the lock. The database unique index provides an additional invariant against multiple current
refresh credentials.

Reusing a consumed digest commits revocation of its family before returning generic HTTP 401.
Every access token and refresh descendant for that family then fails. Other mobile families
and web sessions are unaffected. Random unknown tokens cannot identify/revoke a family.

Two concurrent submissions of the same refresh value produce exactly one HTTP 200 and one
HTTP 401. The second request sees a consumed digest and revokes the family, including the
first request's new credentials. There is deliberately no retry/grace window. A client must
serialize refreshes and atomically replace both locally stored credentials. A lost successful
response followed by retry with the old credential causes revocation and requires fresh MFA.
Logout uses the same lock, so a refresh cannot restore a successfully logged-out family.
Requests authenticated before a revocation cannot be retroactively canceled.

## Audit and operational boundaries

Uses existing best-effort `HttpAccessAudit.SecurityAsync` events with the staff-account resource
and no metadata: `staff.mobile.session.refreshed` for rotation and `staff.mobile.replay.revoked`
for committed replay revocation. The latter's successful outcome describes the revocation,
not a successful login. No raw tokens or digests are copied into audit events or ProblemDetails.

HTTPS, protected shared Data Protection keys, and proxy/logging configuration that excludes
Authorization headers and credential bodies remain deployment requirements. Bearer credentials
are usable by their holder until expiration/revocation. Rate limiting is inherited and per-process.
Audit failure retains the existing sanitized operational signal rather than durable delivery.
Consumed history remains stored: cleanup jobs, device binding, session UI and client secure
storage are explicitly outside this slice. Do not prune history for a live family, since that
would remove replay detection. No notebook, patient context or drawing work is included.

## Files and migration

Updated:

- `backend/Clinic/src/Clinic.Infrastructure/Authentication/MobileStaffAuthentication.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Authentication/MobileStaffSession.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/ClinicDbContext.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/ClinicDbContextModelSnapshot.cs`
- `backend/Clinic/src/Clinic.Api/Controllers/MobileStaffAuthenticationController.cs`
- `docs/doctor-tablet-auth-phase-1a-2a.md` (links to this continuation)

Added:

- `backend/Clinic/src/Clinic.Infrastructure/Authentication/MobileStaffRefreshToken.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260919011815_AddMobileRefreshRotation.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260919011815_AddMobileRefreshRotation.Designer.cs`
- `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffIdentityMobileRefreshHttpTests.cs`
- `docs/doctor-tablet-auth-phase-1a-2b.md`

The additive migration creates the refresh-history table, digest primary key, session foreign
key, filtered unique index and nullable session deadline. It was generated only and was **not
applied to the main ClinicDb**. Integration tests apply migrations only to generated, disposable
`ClinicTests_*` databases.
