# Account API contract

The wire contract for `/api/auth`, written down because it has two
implementations in two repositories and they drifted apart once already.

This service and the browser application in `sw5e-web` were built in parallel
from a written specification. Both had full test suites, both were green, and
they still disagreed about the envelope on `/me`, the spelling of the two-factor
literal, the name of the passkey label field, the capitalisation of every role
name, and the content type of an error document — because each side was tested
against its own idea of the other. This file is the agreed answer, and an
identical copy lives in the web repository.

Everything below was checked against the running QA deployment and against this
source. `AccountWireContractTests` asserts it endpoint by endpoint, naming the
literals rather than deriving them, so that renaming any of them fails the build
here rather than silently breaking a client this repository cannot see.

All bodies are JSON; property names are camelCase exactly as written here.
Errors are RFC 9457 problem documents with `application/problem+json` and a
`detail` string.

## Cross-site request protection

There is **no** CSRF token and no `sw5e_csrf` cookie. The API refuses any unsafe
method whose `Sec-Fetch-Site` is not `same-origin` and whose `Origin` is not in
the configured allow-list, with a bodiless **403**. Confirmed live:

- no `Origin` header -> 403
- `Origin: https://evil.example` -> 403
- `Origin: https://sw5e.cfowers.io` -> 200

The browser sets `Origin` itself on same-origin state-changing fetches, so a
same-origin client needs to do nothing. Clients MUST NOT send `X-CSRF-Token`
and MUST NOT expect a readable CSRF cookie.

## Session

The session is an HttpOnly `__Host-` cookie, `SameSite=Strict`, `Secure`.
Never readable from script. `GET /api/auth/me` is the only way to read it.

Short-lived server-written state also travels in HttpOnly cookies:
`__Host-sw5e.enrol` (enrolment ticket), `__Host-sw5e.pk-register`,
`__Host-sw5e.pk-login` (WebAuthn challenges).

---

## `POST /api/auth/register` — 202

Request: `{ "email": string, "displayName": string }`

Response (identical whether or not the address exists):
```json
{ "status": "pending", "message": "If that address can be registered, ..." }
```

## `POST /api/auth/email/verify` — 200

Request: `{ "email": string, "token": string }`  <- **both fields required**

Response:
```json
{ "status": "verified", "enrollmentExpiresAt": "2026-08-30T19:52:11.123+00:00" }
```

**Verification does not sign you in.** It sets the enrolment-ticket cookie,
which authorises `passkey/register/begin` and `passkey/register/complete` for
the next 10 minutes and nothing else. `GET /api/auth/me` still answers 401.
This is how a new account enrols its first passkey; there is no dead end.
Invalid/expired token -> 400.

## `POST /api/auth/passkey/register/begin` — 200

No request body. Authorised by a session **or** an enrolment ticket.

Response is the WebAuthn creation options document **unwrapped** — there is no
`publicKey` envelope. Top-level keys are `rp`, `user`, `challenge`,
`pubKeyCredParams`, `timeout`, `excludeCredentials`, `authenticatorSelection`,
`attestation`, `hints`, `extensions`. It is exactly what
`PublicKeyCredential.parseCreationOptionsFromJSON()` accepts.

401 when the caller has neither a session nor a ticket.

## `POST /api/auth/passkey/register/complete` — 201

Request: `{ "credential": <PublicKeyCredential.toJSON()>, "name": string|null }`
— the label field is **`name`**, not `label`.

Response:
```json
{ "credentialId": "base64url", "name": "Work laptop", "createdAt": "2026-...Z" }
```
`name` may be null. Completing enrolment does **not** sign you in; the client
follows it with an ordinary passkey sign-in.

## `POST /api/auth/passkey/login/begin` — 200

Request body is ignored entirely. The API never accepts an email address here
and always answers with an empty `allowCredentials`, so the response is
identical for every caller. **Clients must not offer an email field.**

Response is the request-options document **unwrapped**:
```json
{ "challenge": "...", "timeout": 120000, "rpId": "sw5e.cfowers.io",
  "allowCredentials": [], "userVerification": "required", "hints": [] }
```

## `POST /api/auth/passkey/login/complete` — 200

Request: `{ "credential": <PublicKeyCredential.toJSON()> }`

Response is one of:
```json
{ "status": "authenticated", "user": { ...CurrentUser } }
{ "status": "mfaRequired", "user": null }
```
Note the literal is **`mfaRequired`** (camelCase, no hyphen), and the
`mfaRequired` branch carries **no** `methods` array and no account detail at
all. Every failure is 401 with the same wording.

## `GET /api/auth/me` — 200

No envelope — the account object is the whole body:
```json
{
  "id": "0198e0...",
  "email": "reader@example.com",
  "displayName": "Jen Ordo",
  "roles": ["community"],
  "twoFactorEnabled": false,
  "passkeys": [
    { "id": "base64url", "name": "Work laptop", "createdAt": "2026-...Z" }
  ]
}
```
- The field is **`twoFactorEnabled`**, not `mfa.totp`.
- `passkeys` is a list, not a count. `name` may be null.
- There is **no `lastUsedAt`**: the framework's passkey record does not track
  one, and inventing a value would be worse than omitting it.
- `roles` is sorted ordinal. The values are **`Community`**, **`Contributor`**,
  **`Administrator`** — capitalised, and the highest one is spelled
  `Administrator`, not `admin`. These are the names seeded into the database
  and used by the authorization policies.

401 with a problem document when there is no session.

## `DELETE /api/auth/passkey/{credentialId}` — 200

Requires a session. `credentialId` is the base64url id, percent-encoded into
the path.

Response: `{ "status": "removed" }`

- 401 no session
- 404 no such credential on this account
- 409 `{ "code": "last-credential" }` when it is the only credential left —
  removing it would strand the account.

## `POST /api/auth/mfa/totp/enroll` — 200

Requires a session. No request body.
```json
{ "sharedKey": "abcd efgh ijkl mnop", "authenticatorUri": "otpauth://totp/..." }
```
Fields are **`sharedKey`** and **`authenticatorUri`**. Two-factor is not on yet.

## `POST /api/auth/mfa/totp/verify` — 200

Request: `{ "code": "123456" }`

One endpoint, two jobs, selected by server-written cookie state and never by
the body:

- caller holds the pending two-factor cookie (mid sign-in):
  `{ "status": "authenticated", "user": { ...CurrentUser } }`
- caller has a session (finishing enrolment):
  `{ "status": "enabled", "recoveryCodes": ["...", ... 10 items] }`

The enrolment literal is **`enabled`**, not `enrolled`. Recovery codes **are**
returned, exactly once, and only here. Wrong code -> 400 on the enrolment
branch, 401 on the sign-in branch.

## `POST /api/auth/logout` — 204

Anonymous and idempotent. Clears the session plus every half-finished flow.

## `PUT /api/auth/admin/users/{userId}/roles` — 200

Administrators only. Declares the full desired role set; anything absent is
revoked.

Request: `{ "roles": ["Contributor"] }` — only `Contributor` and
`Administrator` may be assigned. `Community` is the floor every account stands
on and is rejected.
Response: `{ "userId": "guid", "roles": ["Community", "Contributor"] }`

400 unknown role, 401, 403 not an administrator, 404 no such account.
