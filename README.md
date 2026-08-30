# sw5e-api

Backend API for the SW5e community platform.

## Requirements

- .NET SDK 10.0.302 or later
- Docker, for PostgreSQL 17. The account tests start one through Testcontainers
  and fail without a running daemon. The content persistence tests also need
  one, but skip themselves when none is reachable so the rest of the suite still
  runs; CI fails the build if that skip happens on a runner that has a daemon.
  Neither is needed to build, or to run the API against the file-backed content
  store.

## Getting started

```bash
dotnet restore
dotnet test
dotnet run --project src/Sw5e.Api
```

The API listens on the port reported at startup. `GET /health` is the liveness
probe. In development, the OpenAPI document is served at `/openapi/v1.json`.

## Project layout

| Project | Responsibility |
|---|---|
| `Sw5e.Api` | Endpoints, composition root, HTTP concerns |
| `Sw5e.Domain` | Content graph model and rules |
| `Sw5e.Infrastructure` | Content persistence and search |
| `Sw5e.Identity` | Accounts, roles, passkeys, and their own DbContext and schema |
| `Sw5e.Migrator` | Deploy-time job: applies content migrations, then imports content |
| `Sw5e.Email` | Email abstraction and provider adapters |

Endpoints are organized as vertical feature slices under `Features/`. Each
feature folder owns its endpoint, request and response types, and handler.

## Content API

Four anonymous, read-only endpoints serve the game content:

| Endpoint | Purpose |
|---|---|
| `GET /api/content-types` | The type registry with live item counts. The site builds its navigation from this. |
| `GET /api/content/{type}` | A paginated, filterable, sortable list of one type. |
| `GET /api/content/{type}/{key}` | One item in full, exactly as it validates against its JSON Schema. |
| `GET /api/search?q=` | Free-text search across every type, grouped by type. |

List parameters: `page` (default 1), `pageSize` (default 25, maximum 100),
`name` (substring filter, maximum 100 characters), `source`, `contentSet`,
`sort` (`name`, `key`, `sourceKey` or `contentSet`) and `direction` (`asc` or
`desc`). Search parameters: `q` (2–100 characters, required), `types` (a
comma-separated subset) and `limit` (results per type, default 5, maximum 25).
Anything outside those bounds is refused with Problem Details rather than
silently clamped, so a client cannot paginate against limits it was never told
about.

Content responses carry an `ETag` and `Cache-Control: public, max-age=300`, and
honour `If-None-Match`.

### Where the content comes from

The endpoints depend on `IContentRepository` in `Sw5e.Domain`, not on any
particular store. Two implementations satisfy it, and which one is in use is a
single setting:

| `Content:Store` | Implementation | What it needs |
|---|---|---|
| `file` (default) | `FileContentRepository` — an in-memory index built at startup by scanning the JSON content files | A content directory |
| `database` | `DbContentRepository` — PostgreSQL | A connection string, a migrated schema, and an import |

Anything else is refused at startup rather than treated as the default, so a
typo in a deploy variable cannot silently leave production on the wrong store.

The two are meant to be interchangeable, and
`tests/Sw5e.Persistence.Tests.Integration/StoreParityTests.cs` holds them to it:
every case there runs both stores over the same corpus and compares them to each
other, rather than to two sets of hand-written expectations that could drift
apart.

Point either store at a content directory with `Content:RootPath`. A relative
path resolves against the application's content root:

```
Content__RootPath=/srv/sw5e/content
```

The default in `appsettings.Development.json` is the sibling `sw5e-database`
checkout. In `file` mode a missing, empty or partially populated directory is
not an error: the API starts, logs what it skipped, and serves an empty or
partial catalogue. The integration tests use a fixture committed under
`tests/Sw5e.Api.Tests.Integration/TestContent`, so they never depend on that
sibling checkout.

## Accounts

Authentication is by **passkey**, with an optional authenticator-app second
factor. There are no passwords: no endpoint sets one, no endpoint checks one,
and the `PasswordHash` column the framework's schema defines stays null for
every account this platform creates.

| Endpoint | Who may call it |
|---|---|
| `POST /api/auth/register` | anyone |
| `POST /api/auth/email/verify` | anyone, with the token from the emailed link |
| `POST /api/auth/passkey/register/begin` | a signed-in account, or a verified address inside its enrolment window |
| `POST /api/auth/passkey/register/complete` | as above |
| `POST /api/auth/passkey/login/begin` | anyone |
| `POST /api/auth/passkey/login/complete` | anyone |
| `POST /api/auth/mfa/totp/enroll` | a signed-in account |
| `POST /api/auth/mfa/totp/verify` | a signed-in account enrolling, or a caller awaiting a second factor |
| `POST /api/auth/logout` | anyone |
| `GET /api/auth/me` | a signed-in account |
| `PUT /api/auth/admin/users/{userId}/roles` | an administrator |

### The one way in

A passkey assertion, followed by whatever second factor the account has, is the
**only** thing that issues a session cookie. Verifying an email address does not
sign anybody in. Registering a passkey does not sign anybody in either — the
client follows enrolment with an ordinary sign-in. That leaves a single code
path to audit, and it is why an account that switched on two-factor
authentication cannot be entered by a route that skips it.

The framework's `SignInManager.PasskeySignInAsync` is deliberately not used: it
completes sign-in with `bypassTwoFactor` set, which is defensible on the
reasoning that a user-verifying passkey is already two factors, and would have
made this platform's TOTP option decorative for everyone who enabled it.

### Registering, and recovering

`register` takes an address and a display name and answers `202` with the same
body every time — whether the address was free, already belonged to an
unverified account, or already belonged to a verified one. It cannot say "that
address is taken" without confirming to a stranger that somebody has an account
here, so it says nothing and emails the account holder instead:

- a free address gets a verification link;
- an unverified account gets its verification link again;
- a **verified** account gets a passkey recovery link, phrased for somebody who
  did not ask for it.

That last case is the recovery flow. Somebody who has lost every device they
enrolled registers again with the same address, and the link that arrives lets
them enrol a fresh passkey. Redeeming any of these links rotates the account's
security stamp, which invalidates every other outstanding link for it and drops
any session already open — the correct outcome when somebody has just proved
mailbox control in order to re-credential.

Sign-in requires **discoverable** passkeys, so `passkey/login/begin` takes no
identifier and returns an empty `allowCredentials` list. The browser picks the
account; the server is never told who is signing in before the signature
arrives. There is no input to vary, so there is nothing to enumerate.

### Roles

| Role | Grants |
|---|---|
| `Community` | The default for every account. Nothing beyond what an anonymous visitor has; content stays read-only. |
| `Contributor` | May upload base game rules and content. Granted by an administrator, never on request. |
| `Administrator` | Everything, including granting and revoking the other roles. |

The first administrator comes from `Identity:BootstrapAdministratorEmail`. That
setting creates nothing: the named person registers through the ordinary public
flow and proves control of the address like anybody else, and the setting only
decides which of the resulting accounts is promoted, on the next start. It is
therefore safe if it leaks.

An administrator cannot remove their own administrator role — the role is the
only thing that can grant the role, so the last one revoking themselves would
leave the platform with no way to appoint another.

### Configuration

| Variable | Required | Notes |
|---|---|---|
| `ConnectionStrings__Sw5eIdentity` | **yes** | PostgreSQL, for the `identity` schema. `Identity__ConnectionString` overrides it, and `ConnectionStrings__Sw5e` is the fallback. **The API refuses to start without one of them** — an API that boots happily with no account system is one serving an unauthenticated site without saying so. |
| `Identity__PublicSiteUrl` | **yes, to send mail** | The public base URL of the browser application, used to build emailed links. Configured rather than derived from the request: deriving it would let anyone who can set a `Host` header decide where a recovery link points. |
| `Identity__RelyingPartyId` | in every deployed environment | The registrable domain passkeys are bound to — `sw5e.example`, with no scheme, port or path. Unset, the framework uses the request's own host, which is right for `localhost` and wrong for anything served under more than one hostname. **Changing it invalidates every existing passkey.** |
| `Identity__AllowedOrigins__0` | only for a separately hosted front end | Exact origins, compared exactly, no wildcards. Empty means same-origin only, which is correct when the site and the API share a hostname behind the proxy. Read by both the WebAuthn origin check and the cross-site request check. |
| `Identity__SessionLifetime` | no | Sliding; `08:00:00` by default. |
| `Identity__EmailTokenLifetime` | no | `01:00:00` by default. |
| `Identity__InitializeDatabaseAtStartup` | no | Off by default. Migrations are a deliberate, separate step in production; a web process that migrates its own database holds schema rights at runtime and races its own replicas. |
| `Identity__BootstrapAdministratorEmail` | no | See above. |
| `Auth__RateLimits__SensitiveAttempts` | no | Attempts per window against a guessable endpoint. `20` per five minutes by default, per client address **and** per endpoint. |
| `Auth__RateLimits__StandardRequests` | no | `120` per minute by default. |

The identity tables live in their own `identity` schema, created by the
migration in `Sw5e.Identity`. Apply it before serving traffic:

```bash
dotnet ef database update --project src/Sw5e.Identity
```

Data protection keys — which sign the session cookie, the two-factor cookie, the
passkey challenge cookies and every emailed token — are persisted into that
schema rather than to the container's file system. On the default file-system
key ring they would be lost on every restart, silently logging every user out
and invalidating every outstanding verification link, and two replicas would
reject each other's cookies.

Account email goes through `IAccountEmailSender`, which `Sw5e.Identity` defines
and `ProviderAccountEmailSender` bridges onto the email library — so the
identity code never learns which provider is configured, and the mail code never
learns what a passkey is. Verification is delegated to `IAccountEmailService`,
whose message is exactly right for it; the passkey recovery and security-notice
messages are composed alongside, because a passwordless site must not send a
"choose a new password" email. Every path turns an undelivered message into a
failure rather than reporting success, since registration that claims to have
sent nothing is indistinguishable, to the user, from an attacker's request being
quietly dropped.

## Persistence

PostgreSQL 17. The content schema lives in `Sw5e.Infrastructure/Persistence`,
behind one registration:

```csharp
builder.Services.AddSw5ePersistence(builder.Configuration);
builder.Services.AddDatabaseContentStore();
```

`AddSw5ePersistence` owns the shared plumbing — the connection string, a single
`NpgsqlDataSource`, the pooled context factory and the database health check.
Everything that needs the database resolves that one data source, so there is
one pool and one place a credential is read.

### The schema, and why it looks like this

Three tables in a `content` schema.

| Table | Holds |
|---|---|
| `content_item` | One row per document: its identity, the columns queries filter and order on, and the document itself as `jsonb` |
| `content_reference` | One row per cross-reference found in a document, resolved where the target exists and recorded as intent where it does not |
| `content_type` | The type registry, seeded by migration so the type column can carry a foreign key |

**The document is stored whole, not shredded into a table per type.** The nine
SW5e content types have very little in common below the surface — a species has
`traits[]` and markdown lore, a monster has a nested stat block, equipment
changes shape depending on whether it is a weapon or armour — and normalising
all of it is roughly forty tables that no endpoint queries. It would also cost
something specific: the published contract for `GET /api/content/{type}/{key}`
is that the response body *is* the type's JSON Schema, passed through unaltered.
A shredded model has to reassemble that on the way out, which means the schema
is written down twice with nothing keeping the two equal, and a field added in
the content repository disappears from the API until somebody notices. Storing
the document as `jsonb` keeps the JSON Schema the single definition of what a
content item is.

**What a document store cannot do is bought back with projected columns.** Name,
source, content set, the folded copies used for case-insensitive matching, and
the display fields a list row needs are lifted out of the document into real,
indexed columns. They are derived on every write by the same projection code the
file-backed store uses, so they cannot drift from the document — re-running the
importer rebuilds them.

**Cross-references are lifted into rows.** That is the part that justifies a
database rather than a directory of files. The eventual goal includes generating
print-ready documents from arbitrary collections, and every question that
implies — "everything published in this book", "the archetype this feature
belongs to", "the feats this background offers" — is a graph traversal.
Answering it from documents means one round trip per edge with every type's link
fields hard-coded into the walker; answering it from `content_reference` is a
join.

An edge whose target is missing is still a row. Exactly one field in the whole
corpus points at another item by slug (`sourceKey`); everything else names its
target by display name, because the documents were transcribed from print. Some
of those targets have not been written yet, and three of the target types —
`class`, and the weapon and armour property types — do not exist as content
types at all. So `content_reference` records what the document said and fills in
`resolved_item_id` only when the target is actually there, which turns "what is
this corpus still missing" from a grep into a query. Resolution is recomputed
across the whole graph on every import, so authoring the missing item reconnects
the edges that were waiting for it without those documents changing.

Every text column is declared `COLLATE "C"`. Under a locale collation,
PostgreSQL weights punctuation differently from `StringComparer.Ordinal`, so the
same page of species comes back in a different order depending on which store
answered and on the locale the database was created with. Byte order is the only
collation the two stores can agree on.

### Migrations and the migrator

Migrations are **never applied on startup**. Every replica runs startup, so N
replicas would race to apply the same migration; a rolling deploy would run old
and new code against whichever schema won; and the schema would end up changed
by whoever happened to restart a container. Instead `Sw5e.Migrator` is a job with
an exit code:

```bash
dotnet run --project src/Sw5e.Migrator -- migrate   # apply migrations only
dotnet run --project src/Sw5e.Migrator -- import    # load content only
dotnet run --project src/Sw5e.Migrator -- all       # both, in that order
```

It ships inside the API image, so the two are always built from the same commit:

```bash
docker run --rm --entrypoint dotnet \
  -e "ConnectionStrings__Sw5e=Host=db;Database=sw5e;Username=sw5e;Password=..." \
  -e Content__RootPath=/srv/content \
  -v /srv/sw5e/content:/srv/content:ro \
  ghcr.io/christopherfowers/sw5e-api:latest Sw5e.Migrator.dll all
```

Exit codes: `0` success, `1` a command failed, `2` the command was not
recognised.

Because nothing migrates on startup, a deploy that ships new code and forgets
the migrator would otherwise be discovered by a user. `GET /health/ready`
reports that state as `degraded` with the number of missing migrations.

To scaffold a migration:

```bash
dotnet ef migrations add <Name> \
  --project src/Sw5e.Infrastructure \
  --startup-project src/Sw5e.Migrator \
  --output-dir Persistence/Migrations \
  --context Sw5eContentDbContext
```

No database is needed for that: a design-time factory supplies a deliberately
unusable connection string, so a command that genuinely needs a database fails
to connect rather than quietly reaching a real one.

### The importer

`ContentImporter` loads the canonical JSON into PostgreSQL and can be run again
over the same corpus without changing anything — each document's content hash is
compared with the stored one and a row is written only when it differs. That
matters because deploys get retried: an importer that deleted and re-inserted
would churn every row and invalidate every cached response in front of the API
for a corpus that did not change.

It refuses to interpret a failed read as a deletion. An import that finds no
content deletes nothing, and an import that finds no content *for a type* leaves
that type alone — an unmounted volume and an emptied corpus are
indistinguishable from inside the importer, and the first is far more likely.
The migrator turns "found nothing at all" into a non-zero exit so the deploy
stops rather than publishing an empty catalogue.

### Health

| Endpoint | Answers |
|---|---|
| `GET /health` | Liveness. Never consults a dependency, so a database outage does not make an orchestrator restart every API container. This is the probe the image's `HEALTHCHECK` uses. |
| `GET /health/ready` | Readiness. `healthy`, `degraded` (reachable, schema behind this build) or `unhealthy` (unreachable), with one entry per check. |

Neither ever returns a connection string, a host name or a stack trace.

## Security

Every response carries a restrictive baseline of security headers, applied by
`SecurityHeadersMiddleware` before any other middleware so that error responses
are covered. See [SECURITY.md](SECURITY.md) for reporting instructions.

### Sessions

The session cookie is `__Host-sw5e.session`: `HttpOnly`, `Secure` in every
environment, and `SameSite=Strict`. The `__Host-` prefix is not decoration — a
browser refuses to store such a cookie unless it is `Secure`, has `Path=/` and
carries no `Domain`, so no sibling subdomain can set it and nothing served over
plain HTTP can either.

A cookie rather than a bearer token, because a token has to live somewhere
JavaScript can reach it, which makes any cross-site scripting bug anywhere on
the origin an immediate credential theft. `SameSite=Strict` rather than `Lax`,
because `Lax` still attaches the cookie to top-level cross-site navigations.

Sessions are re-checked against the account's security stamp every five minutes
rather than the framework's default thirty, so a revoked role or a locked
account drops a live session in minutes rather than at expiry.

### Cross-site request forgery

Three independent layers, none of which is a token shipped to JavaScript:

1. `SameSite=Strict`, so a request initiated from another site arrives without
   the cookie and is refused as unauthenticated.
2. An origin check on every unsafe request, which refuses anything whose
   `Origin` is not an origin this deployment serves. It fails closed: a request
   with neither `Origin` nor `Sec-Fetch-Site` is refused, because every browser
   has sent `Origin` on unsafe requests for years.
3. A JSON body. HTML forms — the only way to make a browser issue a
   cross-origin `POST` without CORS approval — can send exactly three content
   types, and `application/json` is not among them.

### Brute force

Lockout is five failed attempts and fifteen minutes, and applies to new accounts
too; the framework's default exempts them, which would leave the freshest
accounts as the only ones with unlimited attempts. Rate limits are partitioned
by client address *and* endpoint, so hammering sign-in cannot exhaust somebody
else's ability to register.

Lockout answers the per-account attack and rate limiting answers the per-caller
one — an attacker spreading a single guess across ten thousand accounts trips no
lockout counter at all.

### What errors do not say

Every authentication failure gives the same answer: unknown credential, invalid
signature, expired challenge, unverified address, locked-out account. Reporting
a lockout confirms the account exists and tells somebody running a lockout
attack that their denial of service is working. The server log distinguishes all
of these in full, keyed by account identifier.

### Deny by default

Every mapped endpoint without an explicit policy is refused. This is the
opposite of the framework's default, where an endpoint with no `[Authorize]` is
public — fine for a mostly public site, and the wrong way round where forgetting
is a breach. The genuinely public endpoints all say `AllowAnonymous` explicitly.
Requests that match no endpoint still answer `404` rather than `401`.

The `type` and `key` route values are the only caller-controlled strings
anywhere near a path join. `type` is resolved against a closed, compile-time
registry and `key` must match the slug pattern the schemas define; both are
checked before any store is asked anything, and what is carried forward is the
registry entry rather than the caller's own string. Error responses never
disclose a filesystem path, a stack trace or an internal identifier.

## Container image

`Dockerfile` builds the API into an image published to the GitHub Container
Registry as:

```
ghcr.io/christopherfowers/sw5e-api
```

| Tag | Points at |
|---|---|
| `sha-<40-char commit SHA>` | One specific commit. This is what a deploy resolves; `deploy.sh` takes `sha-${GITHUB_SHA}` and refuses anything else. |
| `latest` | The most recent build of `main`. A convenience fallback for local pulls, never a deploy target. |
| `1.2.3`, `1.2`, `1` | A pushed `v1.2.3` release tag |

`.github/workflows/release.yml` builds `linux/amd64` and `linux/arm64` and
pushes on every commit to `main`, attaching build provenance and an SBOM to
the manifest. Pull requests build the same image without pushing it, start a
container from it and check the health endpoint, so a Dockerfile change is
exercised before it merges.

### What the image expects

| | |
|---|---|
| Port | `8080`, HTTP only. The image never listens on 80, and nothing in it binds a privileged port. |
| User | Non-root: UID `1654` (`app`), provided by the .NET base image. Nothing in the image is writable by it. |
| Content | A read-only mount at `/srv/content`. |
| Health | `HEALTHCHECK` requests `GET /health` from inside the container every 30s, after a 15s start period. |
| TLS | None. The image speaks plain HTTP and must sit behind a TLS-terminating proxy — see [Deployment](#deployment), which is **required** reading before the first boot. |
| Base images | `mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23` to build, `mcr.microsoft.com/dotnet/aspnet:10.0.11-alpine3.23` to run. Both are Microsoft's MIT-licensed .NET images on Alpine. |

No secret, connection string or certificate is baked into the image. Everything
below is supplied at run time.

### Environment variables

| Variable | Default in the image | Notes |
|---|---|---|
| `ASPNETCORE_URLS` | `http://+:8080` | Set by the image. Overriding it is how you move the listener; `EXPOSE` and the healthcheck both assume 8080. |
| `Content__Store` | `file` | `file` or `database`. Anything else fails startup. |
| `Content__RootPath` | `/srv/content` | Where the content volume is mounted. A relative value resolves against the app's content root (`/app`). Read by the `file` store and by the migrator's import step. |
| `ConnectionStrings__Sw5e` | unset | The one connection string for the whole database. **Required when `Content__Store=database`**, and by the migrator always. It carries the password, so it comes from the environment and never from a committed file. |
| `Sw5e__Database__CommandTimeoutSeconds` | `15` | Per-command timeout. Every query the API issues is a single-page read; one that has not answered in fifteen seconds is stuck, not slow. |
| `Sw5e__Database__MaxRetryCount` | `3` | Retries for transient failures only — a dropped connection, a failover. A constraint violation or a syntax error is never retried. |
| `Sw5e__Database__ReportPendingMigrations` | `true` | Whether readiness reports a schema behind this build as degraded. |
| `ASPNETCORE_ENVIRONMENT` | unset, so `Production` | `Development` turns off HSTS and HTTPS redirection and serves the OpenAPI document. Do not set it in a deployed stack. |
| `Email__Provider` | unset | `MailerSend`, `Smtp` or `Capture`. **Required outside Development** — the app refuses to start without it. See [Email](#email). |
| `Email__FromAddress` | unset | The sending mailbox. Required whenever `Email__Provider` is set. |
| `Email__MailerSend__ApiToken` | unset | **Secret.** Required when the provider is `MailerSend`. Never bake it into the image. |
| `Email__Smtp__Host` | unset | Required when the provider is `Smtp`. |
| `Email__Smtp__Password` | unset | **Secret.** Required when `Email__Smtp__UserName` is set. |
| `ForwardedHeaders__KnownNetworks__0` | unset | The proxy's network in CIDR notation. **Required behind a containerised proxy** — see below. |
| `ForwardedHeaders__KnownProxies__0` | unset | An exact proxy IP, as an alternative to the network above. |
| `HTTPS_PORT` | unset | Port `UseHttpsRedirection` redirects to. Leave it unset and let the proxy handle the HTTP-to-HTTPS redirect at the edge. |
| `Logging__LogLevel__Default` | `Information` | Standard ASP.NET Core logging configuration; every `Logging__*` key binds. |
| `AllowedHosts` | `*` | Host filtering, if you want it narrower than the proxy's routing rule. |
| `DOTNET_EnableDiagnostics` | `0` | Set by the image: the diagnostic IPC socket has no use in this container. |
| `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` | `true` | Set by the Alpine base image, which ships no ICU. Safe here — every comparison, sort and case fold in the content index is ordinal or invariant — but culture-sensitive behaviour is unavailable if future code wants it. |
| `ASPNETCORE_HTTP_PORTS` | `8080` | Set by the base image and ignored while `ASPNETCORE_URLS` is set. |

Configuration keys nest with a double underscore and arrays are indexed, so
`ForwardedHeaders:KnownNetworks[0]` is `ForwardedHeaders__KnownNetworks__0`.

### The content volume

The image points `Content:RootPath` at `/srv/content`, the path the compose
stack mounts its shared content volume on. That volume is populated by the
sw5e-database init container; the API only ever reads it, so mount it `:ro`.

The container runs as UID 1654, so files in the volume must be readable by
that user — world-readable files are the simplest way to guarantee it. A
missing, empty or unreadable directory is not fatal: the API starts anyway and
serves an empty catalogue, logging `Content directory ... does not exist` or
`Content index built with 0 items.` A healthy container serving an empty
catalogue is therefore the signature of a content volume that did not mount,
not of a broken API — check the startup log rather than the health endpoint.

### Running it directly

```bash
docker run --rm -p 8080:8080 \
  -v /srv/sw5e/content:/srv/content:ro \
  -e Email__Provider=Capture \
  -e Email__FromAddress=noreply@example.com \
  ghcr.io/christopherfowers/sw5e-api:latest
```

The image runs as Production, so it will not start without an email provider —
see [Email](#email). `Capture` is the credential-free choice for a local run:
it writes messages to the log and delivers nothing.

`GET /health` answers `{"status":"healthy"}` and is the same endpoint the
image's own healthcheck probes.

## Email

Transactional mail — address verification and password reset — lives in
`src/Sw5e.Email`, behind an `IEmailSender` seam so that swapping provider is a
configuration change rather than a code change. MailerSend's HTTP API is the
intended production provider; a generic SMTP relay and an in-memory capture
provider satisfy the same interface.

[`src/Sw5e.Email/README.md`](src/Sw5e.Email/README.md) documents the seam, every
configuration key, and the contract the account flows consume.

Two things matter at the deployment level:

- **`Email__Provider` and `Email__FromAddress` are required outside
  Development**, along with whatever credential the chosen provider needs. A
  missing value throws at startup and the host does not come up. That is
  deliberate: the alternative is a deployment that looks healthy, returns a
  failure on every send that nobody reads, and is discovered by a locked-out
  user whose reset email never arrived. Set `Email__Provider=Capture` in an
  environment that should not send real mail yet — it logs messages and
  delivers nothing.
- **Credentials never live in the repository or the image.** The MailerSend API
  token and the SMTP password have no defaults and no committed placeholder.
  Supply them as environment variables from the deployment's secret store.

In Development, with nothing configured at all, the app runs on the capture
provider: it logs the recipient and subject of each message and delivers
nothing, so no credentials are needed to work on the account flows locally. It
does not log message bodies — a verification or reset link is a bearer
credential and does not belong in a log. To open one, run a local catcher such
as Mailpit and point the SMTP provider at it; `src/Sw5e.Email/README.md` has the
four settings.

## Deployment

The API is designed to run behind a TLS-terminating reverse proxy — Azure App
Service's front end, or Traefik or nginx in front of the container in a Docker
deployment. The proxy terminates HTTPS and forwards the request to Kestrel as
plain HTTP, adding `X-Forwarded-Proto` and `X-Forwarded-For` headers so the
app can recover the original scheme and client address.

### You must configure the trusted proxy allow-list

`Program.cs` wires up `ForwardedHeadersMiddleware` to honour those two
headers, but ASP.NET Core will only apply them if the request comes from a
proxy it trusts. By default that trust list is **loopback only**, and that
default is deliberate: without it, any client that can reach Kestrel directly
could set `X-Forwarded-Proto: https` or `X-Forwarded-For: <anything>` on its
own request and spoof its scheme or source address.

For any real deployment the proxy is not loopback, so you must widen the
trust list explicitly through configuration. Two keys bind, both read as
string arrays:

| Configuration key | Bound to | Example value |
|---|---|---|
| `ForwardedHeaders:KnownProxies` | `ForwardedHeadersOptions.KnownProxies` — exact proxy IP addresses | `10.0.0.4` |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeadersOptions.KnownIPNetworks` — proxy IP ranges in CIDR notation | `10.0.0.0/16` |

As environment variables (App Service application settings, Docker
environment variables, or any other `IConfiguration` provider that maps `:`
to `__`), array entries are indexed:

```
ForwardedHeaders__KnownProxies__0=10.0.0.4
ForwardedHeaders__KnownNetworks__0=10.0.0.0/16
```

`KnownNetworks` entries must be valid CIDR notation with no host bits set in
the base address (e.g. `10.0.0.0/16`, not `10.0.0.4/16`) — a malformed value
fails at startup rather than silently widening or narrowing the trusted
range. Add one indexed entry per proxy or network as needed
(`ForwardedHeaders__KnownProxies__1`, `__2`, and so on).

Do not work around a misconfigured proxy by leaving both keys empty unless
the app is genuinely unreachable except through that proxy — an empty trust
list makes the middleware accept forwarded headers from every caller.

#### What happens if you skip this

Nothing fails loudly. With the trust list left at its loopback-only default
behind a real proxy, the forwarded headers are silently ignored:
`Request.IsHttps` stays `false` for every request, so the HSTS middleware
never emits a `Strict-Transport-Security` header, and HTTPS redirection can
issue a redirect the proxy forwards straight back as HTTP, producing a
redirect loop. There is no test, health check, or log line that catches
this — the app keeps responding `200 OK` on `/health` the whole time. The
only visible symptom is a missing security header.

#### Azure App Service

Set the two keys above as Application Settings (Configuration blade or `az
webapp config appsettings set`), using the double-underscore array syntax
shown above. Set `ForwardedHeaders__KnownProxies__0` (or `KnownNetworks`) to
the address or range of App Service's front-end infrastructure, or of any
load balancer you have placed in front of it. If you don't control that
address space directly, consult your network/App Service configuration
rather than guessing — an overly broad range defeats the purpose of the
allow-list.

#### Docker Compose behind Traefik

This is the case the QA stack runs, and the one most likely to break on first
boot, so it is worth being exact about.

Traefik is not loopback. It reaches the API over a user-defined bridge
network, so the remote address Kestrel sees is Traefik's container IP on that
network — something like `172.28.0.3`, never `127.0.0.1`. With the default
loopback-only trust list, `ForwardedHeadersMiddleware` discards Traefik's
`X-Forwarded-Proto: https`, `Request.IsHttps` stays `false`, and both
consequences described above follow: no `Strict-Transport-Security` header is
ever emitted, and `UseHttpsRedirection` answers the proxy's plain HTTP request
with a redirect to HTTPS that Traefik forwards straight back as plain HTTP — a
loop the client sees as `ERR_TOO_MANY_REDIRECTS`.

Fix it by trusting the compose network, and pin that network's subnet so the
value you trust cannot drift when Docker reassigns pool addresses:

```yaml
services:
  api:
    image: ghcr.io/christopherfowers/sw5e-api:latest
    environment:
      # The `edge` network below: the network Traefik connects FROM, in CIDR
      # notation, with no host bits set.
      ForwardedHeaders__KnownNetworks__0: "172.28.0.0/16"
    volumes:
      - sw5e-content:/srv/content:ro
    networks: [edge]
    labels:
      traefik.enable: "true"
      traefik.http.routers.sw5e-api.rule: "Host(`api.example.test`)"
      traefik.http.routers.sw5e-api.entrypoints: "websecure"
      traefik.http.routers.sw5e-api.tls.certresolver: "letsencrypt"
      # Traefik must be told the container port; 8080 is what the image binds.
      traefik.http.services.sw5e-api.loadbalancer.server.port: "8080"

networks:
  edge:
    ipam:
      config:
        - subnet: 172.28.0.0/16
```

If you would rather not pin the subnet, read the actual value back from the
running stack instead of guessing:

```bash
docker network inspect <stack>_edge \
  --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
```

`ForwardedHeaders__KnownProxies__0` set to Traefik's container IP works too,
but a container IP changes when the container is recreated, so the network
form is the one to prefer. The default Docker bridge (`172.17.0.0/16`) is not
the right value for a compose stack — compose creates its own network.

Trust only the network the proxy actually connects from. Trusting `0.0.0.0/0`,
or emptying both keys, lets any caller that can reach port 8080 directly claim
its own scheme and client address.

Leave `HTTPS_PORT` unset and configure the HTTP-to-HTTPS redirect on Traefik's
`web` entrypoint instead. Once the trust list is right, requests arrive already
marked as HTTPS and the app's own redirection never fires.

With `HTTPS_PORT` unset the app logs `Failed to determine the https port for
redirect` once at startup. That warning is expected and is the safe state: the
redirection middleware has no port to send clients to, so it forwards the
request instead of redirecting, which is one fewer way to build a loop. Set
`HTTPS_PORT` only if you deliberately want the app rather than the proxy to
issue the redirect — and only with the trust list already correct, or you will
build exactly the loop described above.

Verify with the check below, and confirm in the same pass that Traefik's access
log shows `200` on `/health` rather than a chain of `307`s.

#### Docker Compose behind nginx

The same rule applies unchanged: nginx in a sibling container is not loopback
either, and its address on the compose network — or the gateway address, if
nginx routes through it — must be in the trust list. Confirm the address with
`docker network inspect` as above, then set
`ForwardedHeaders__KnownNetworks__0`. Check that nginx is actually sending the
headers (`proxy_set_header X-Forwarded-Proto $scheme;` and
`X-Forwarded-For $proxy_add_x_forwarded_for;`); Traefik sends them by default,
nginx does not.

#### Verifying it after deploying

Once configured, request the site over HTTPS through the real domain (not
`localhost`, which the HSTS middleware always skips) and inspect the
response headers:

```
curl -I https://your-domain.example/health
```

Confirm the response includes:

```
Strict-Transport-Security: max-age=31536000; includeSubDomains; preload
```

If that header is absent, or an HTTP request to the same host redirects
repeatedly instead of landing on HTTPS, the proxy's address is not in the
trusted list — recheck `ForwardedHeaders:KnownProxies` /
`ForwardedHeaders:KnownNetworks` against the address the proxy actually
connects from.

## License

MIT — see [LICENSE](LICENSE).

## QA deployment

Merging to `main` publishes the image and then deploys it to the internal QA
environment at <https://sw5e.cfowers.io>, which runs the database, API and site
as one Compose stack behind the reverse proxy.

The deploy step runs on a self-hosted runner on the QA host. That runner polls
GitHub outbound — no inbound port is opened — holds no secrets, and is
permitted to run exactly one script via a narrow sudoers rule. Only the
immutable `sha-<full commit SHA>` tag is ever deployed; `latest` is refused.
This repository deploys only the `api` service, so a merge here cannot move
the other two.

The step is gated on the `DEPLOY_ENABLED` repository variable. A job targeting
an unregistered runner label queues indefinitely rather than failing, so until
the runner is registered the gate keeps merges clean. Set `DEPLOY_ENABLED` to
`true` under Settings → Secrets and variables → Actions to turn it on.
