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

## `GET /api/auth/challenge` — 200

Anonymous. Answers on every deployment, whether or not a solution is currently
required, so the client has one code path rather than two.

```json
{
  "salt": "9f2c1e0b7a4d5386c1f0b29e4d7a6853",
  "difficulty": 18,
  "expiresAt": "2026-08-30T19:52:11.1234567+00:00",
  "signature": "3f7c...64 hex characters..."
}
```

`salt` is 32 hex characters. `difficulty` is leading zero **bits**, not hex
characters — a client that counts characters solves sixteen times too much or
four times too little. `expiresAt` and `signature` are opaque and MUST be echoed
back byte for byte: both are covered by the signature, so a client that parses
the date and re-formats it produces a different string and every solution it
sends is refused.

**Solving.** Find the smallest non-negative integer `counter` such that
`SHA-256(UTF8("{salt}:{counter}"))` opens with `difficulty` zero bits. A
challenge is good exactly once and expires; fetch a fresh one per request, and
never cache the response.

**Sending.** Five request headers, on the guarded endpoints below:

| Header | Value |
|---|---|
| `X-Sw5e-Challenge-Salt` | `salt`, verbatim |
| `X-Sw5e-Challenge-Difficulty` | `difficulty`, as a decimal integer |
| `X-Sw5e-Challenge-Expires` | `expiresAt`, verbatim |
| `X-Sw5e-Challenge-Signature` | `signature`, verbatim |
| `X-Sw5e-Challenge-Counter` | the counter, as a plain non-negative decimal integer — no sign, no separators, no spaces |

**Guarded endpoints:** `POST /api/auth/register` and `POST /api/auth/email/code`.
Where the challenge is switched on, a request without a valid, unspent solution
is refused with a **403**:

```json
{
  "status": 403,
  "title": "Challenge required",
  "detail": "This request must carry a solved proof-of-work challenge. ...",
  "code": "challenge-required"
}
```

Branch on `code`. This is not a 429 and must not be treated as one: a 429 means
stop and come back later, while this means solve a fresh challenge and retry
straight away. Every cause — no solution, a wrong counter, an expired challenge,
a salt already spent, a challenge issued before the difficulty was raised — is
this same document, and the right response to all of them is identical.

Where the challenge is switched off the guarded endpoints ignore these headers
entirely, including malformed ones, so a client may always send them.

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

## `POST /api/auth/email/code` — 202

Request: `{ "email": string }`

Response, **identical for every address**:
```json
{
  "status": "pending",
  "message": "If that address can be signed in to, a code is on its way. ...",
  "resendAfterSeconds": 60,
  "expiresInSeconds": 600
}
```

The identical answer is the whole design, not a courtesy. An address with an
account is sent a six-digit code; an address without one is sent a short note
saying somebody tried to sign in and that there is nothing here. Both branches
do one key derivation, one insert and one send, so the response body, the status
code and the elapsed time all say the same thing whether or not the address is
registered. A client must not try to infer anything from any of the three.

The two numbers are constants read from configuration, not facts about the code
that was just issued. `resendAfterSeconds` is what the front end counts down
before re-enabling its resend control; `expiresInSeconds` is the copy on the
entry screen. A value that varied with what this address had recently been sent
would answer, from an anonymous endpoint, a question about somebody else's
sign-in.

**Limits, all enforced server-side and none of them reported to the caller:**

| Limit | Value | Counted against |
| --- | --- | --- |
| Requests per window | 5 per 15 minutes | the caller's IP (429 when spent) |
| Codes per address | 3 per 15 minutes | the address (silent; still 202) |
| Resend cooldown | 60 seconds | the address (silent; still 202) |
| Code lifetime | 10 minutes | the code |
| Attempts per code | 5 | the code |

The per-caller limit is the half that survives an attacker spreading one request
each across ten thousand strangers; the per-address limit is the half that
survives an attacker with ten thousand addresses of their own to send from.
Both are needed. Exhausting an address's budget still answers 202 and simply
sends nothing — answering 429 there would confirm that somebody had recently
asked for a code for that address.

400 for a malformed address. That is the last point at which it is safe to be
specific: the shape of an address is not a fact about whether an account exists.

## When mail cannot be delivered

`register` and `email/code` answer **202 with the body above even when the mail
provider refuses the message.** A relay that is down, misconfigured or rejecting
the sending domain does not change the status code, the body, or the fact that
exactly one message is attempted on each branch. This is not indifference: a
delivery failure that changed the response would be an error the caller could
provoke, and the day one branch stops sending — an unregistered address, say,
whose notice somebody removes as a saving — the difference between the two
answers would be a perfect test of whether an address has an account here.

Clients must therefore not read "202" as "the message was delivered". The
message that goes with it already says *if* that address can be registered;
delivery is a separate fact, and it is not per-address.

Where that fact does appear:

- **The application log**, at error, with the provider's own reply. That reply
  can quote the envelope, so it stops there.
- **`GET /api/health/ready`**, as a check named `account-email`. It reports
  `degraded` — never `unhealthy` — while account mail is failing, and the
  overall response stays **200** so that a mail outage never drains the
  instances still serving the rest of the site. The description names no
  address and does not repeat the provider's reply.

The readiness surface is anonymous and its answer is one global fact, the same
for every reader, which is what makes it safe to show. A front end that wants to
be honest with somebody who has just been told to check their inbox can read it
and say, site-wide, that email is currently delayed — never per address.

## `POST /api/auth/email/code/verify` — 200

Request: `{ "email": string, "code": "123456" }` — **both fields required.** The
address is part of what the stored hash covers, so a code issued for one address
cannot be redeemed against another.

Response, the same two-branch union `passkey/login/complete` answers:

- `{ "status": "authenticated", "user": { ...CurrentUser } }`
- `{ "status": "mfaRequired", "user": null }` — the account has an authenticator
  app, and the client now posts to `POST /api/auth/mfa/totp/verify` exactly as
  it would after a passkey. The literal is **`mfaRequired`**, camel case, no
  hyphen, and the branch carries no account detail whatsoever.

**Every failure is the same 401**: wrong digits, expired code, already-spent
code, code issued for a different address, attempts exhausted, unknown address,
unverified account, locked-out account. The detail string is constant and names
none of them.

A session established this way can reach the account area and **cannot use a
Contributor or Administrator role** — see `GET /api/auth/me` below and the 403
described under the role endpoint.

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
  "roles": ["Community"],
  "twoFactorEnabled": false,
  "passkeys": [
    { "id": "base64url", "name": "Work laptop", "createdAt": "2026-...Z" }
  ],
  "authenticationMethod": "passkey",
  "strongAuthentication": true,
  "secondFactorRequired": false
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

The last three fields describe **this session**, not the account:

- `authenticationMethod` is `"passkey"`, `"totp"` or `"email"` — how the caller
  got in. Null only for a session issued before the field existed.
- `strongAuthentication` is true for `passkey` and `totp` and false for `email`.
  It is derived from the field above and sent anyway, so a client never has to
  keep its own copy of which methods qualify.
- `secondFactorRequired` is true when the account's roles oblige it to hold a
  passkey or an authenticator app — Contributor and Administrator. It says
  nothing about whether the obligation is met; `passkeys` and `twoFactorEnabled`
  answer that.

Reading these off the session rather than off the account is deliberate.
Deciding at request time whether an account *has* a passkey would be satisfied
by an administrator with a passkey who signed in from a library computer with a
mailbox code — which is exactly the case the rule exists to stop.

401 when there is no session. It carries a problem document, but a client must
not depend on that: the refusal is raised by the authentication handler rather
than by the endpoint, and a reverse proxy in front of the service can answer
with no body at all. Decide the outcome from the status code and use the body
only to improve the message.

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
Response:
`{ "userId": "guid", "roles": ["Community", "Contributor"], "awaitingSecondFactor": false }`

`awaitingSecondFactor` is true when the grant landed on an account that has
neither a passkey nor an authenticator app, so it now holds a role it cannot yet
use. The grant is **not** refused — refusing would make the administrator's
action fail for a reason about somebody else's device, and would leave no way to
appoint somebody who has not enrolled yet. Instead it succeeds, the account is
emailed and told what to add, and this flag lets the administrator see the same
thing on screen.

400 unknown role, 401, 403 not an administrator, 404 no such account.

## `GET /api/auth/admin/users` — 200

Administrators only, and only from a session established with a passkey or an
authenticator app. This is the account directory, and it is **the only response
on the platform that carries somebody else's email address**.

Query: `q` (email or display name, case-insensitive, 2–254 characters),
`role` (`Community` | `Contributor` | `Administrator`), `status` (`active` |
`suspended` | `unverified` | `all`), `page`, `pageSize` (default 25, max 100).
An unrecognised `role` or `status`, or a `q` shorter than two characters, is a
**400** rather than an ignored filter.

```json
{
  "users": [
    {
      "id": "guid",
      "email": "reader@example.test",
      "displayName": "Jaina",
      "roles": ["Community"],
      "emailConfirmed": true,
      "twoFactorEnabled": false,
      "secondFactorEnrolled": true,
      "lockedOut": false,
      "suspension": null,
      "createdAt": "2026-09-01T12:00:00+00:00"
    }
  ],
  "page": 1,
  "pageSize": 25,
  "totalCount": 1,
  "totalPages": 1
}
```

`suspension` is `null` or
`{ "at": "...", "reason": "...", "byUserId": "guid" }`. Not suspended is the
*absence* of a suspension, so a client has one question to ask rather than two.

`secondFactorEnrolled` is whether the account holds a passkey or an
authenticator — that is, whether granting it `Contributor` will produce a role
it can actually use. It is not a credential list; no credential identifiers,
public keys or counters leave the store for a directory listing.

401, 403 not an administrator, 403 `strong-authentication-required`.

## `GET /api/auth/admin/users/{userId}` — 200

`{ "user": { ...AdminUser }, "outstandingDrafts": 0 }`

`outstandingDrafts` is `null` on a deployment that serves content from files and
has no authoring at all — not `0`, so an interface does not draw "0 drafts"
beside an account where the concept does not exist. It is the one thing that
will refuse a deletion, which is why it is readable before trying one.

401, 403, 404 no such account.

## `PUT /api/auth/admin/users/{userId}/suspension` — 200

Request: `{ "suspended": true, "reason": "..." }` or `{ "suspended": false }`.

Declarative, like the role grant. `reason` is **required** when suspending and
**refused** when reinstating — there is nowhere to store the second, and
accepting it would mean an administrator writing an explanation that goes
nowhere. The reason is never disclosed to the account it is about.

Response: `{ "userId": "guid", "suspension": { ... } | null }`

A suspended account cannot obtain a session by any route — the passkey
assertion, the emailed code and the authenticator step all answer the same
`401` every other sign-in failure gets — and any session it already had stops
working on its very next request. Its passkeys stay on the account and are
inert, so reinstating restores access rather than requiring re-credentialling.

400 missing flag, missing reason, reason on a reinstatement, own account,
already in that state. 401, 403, 404.

## `DELETE /api/auth/admin/users/{userId}` — 200

Optional body: `{ "reason": "..." }`. In the body rather than a query string,
because a sentence naming a person does not belong in a URL every access log
writes down.

Response: `{ "userId": "guid", "authorshipRetained": true }`

Removes the account and everything identifying it. Does **not** remove what it
wrote: content revisions keep `actorUserId` and moderation reports keep their
reporter identifier, and both render afterwards as a removed account — which on
the flag queue is `reporter.displayName === null`, the state that contract
already documented.

400 own account. 401, 403, 404 no such account. **409** with
`code: "drafts-outstanding"` and a `draftCount` extension while the account owns
unpublished drafts.

## `GET /api/auth/admin/audit` — 200

Every role change, suspension, reinstatement and deletion, newest first. Query:
`subjectId`, `actorId`, `action`, `page`, `pageSize`.

```json
{
  "actions": [
    {
      "id": "guid",
      "action": "roles-changed",
      "actorUserId": "guid",
      "actorDisplayName": "Jaina",
      "subjectUserId": "guid",
      "subjectDisplayName": "Zeb",
      "rolesBefore": null,
      "rolesAfter": ["Contributor"],
      "reason": null,
      "createdAt": "2026-09-01T12:00:00+00:00"
    }
  ],
  "page": 1,
  "pageSize": 25,
  "totalCount": 1,
  "totalPages": 1
}
```

`action` is one of `roles-changed`, `account-suspended`, `account-reinstated`,
`account-deleted` — hyphenated and lower case, which is also what is stored.

The display names are copies taken at the time, so an entry stays readable after
either account has gone; that is what makes the `account-deleted` entry worth
anything. Email addresses are never copied into the log.

`rolesBefore` and `rolesAfter` list only assignable roles, so `null` means "held
no assignable role" as well as "this action was not about roles". `action` is
what tells those apart.

### The `strong-authentication-required` 403

Contributor and Administrator actions require the session to have been
established with a passkey or an authenticator code. A session established with
an emailed code alone is refused with:

```json
{
  "status": 403,
  "title": "Stronger sign-in required",
  "detail": "This action needs a passkey or an authenticator app. ...",
  "code": "strong-authentication-required"
}
```

Branch on `code`, not on the wording. The generic 403 — "your account does not
have access" — would be both wrong and unactionable here, because the account
does have access; it is this sign-in that does not. A client that sees this code
should offer to sign in again with a passkey when the account has one, and offer
enrolment when it does not; `GET /api/auth/me` carries what it needs to tell
those apart.
