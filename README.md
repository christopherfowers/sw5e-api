# sw5e-api

Backend API for the SW5e community platform.

## Requirements

- .NET SDK 10.0.302 or later
- Docker (for PostgreSQL 17; not required for the current test suite)

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
| `Sw5e.Infrastructure` | Persistence, search, identity |
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
particular store. The implementation in use is `FileContentRepository`, which
builds an in-memory index at startup from the JSON content files maintained in
the `sw5e-database` repository. A PostgreSQL implementation of the same
interface replaces it later; the interface takes filtering, ordering and paging
as query parameters precisely so that swap is a registration change rather than
a rewrite.

Point the API at a content directory with `Content:RootPath`. A relative path
resolves against the application's content root:

```
Content__RootPath=/srv/sw5e/content
```

The default in `appsettings.Development.json` is the sibling `sw5e-database`
checkout. A missing, empty or partially populated directory is not an error: the
API starts, logs what it skipped, and serves an empty or partial catalogue. The
integration tests use a fixture committed under
`tests/Sw5e.Api.Tests.Integration/TestContent`, so they never depend on that
sibling checkout.

## Security

Every response carries a restrictive baseline of security headers, applied by
`SecurityHeadersMiddleware` before any other middleware so that error responses
are covered. See [SECURITY.md](SECURITY.md) for reporting instructions.

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
| `Content__RootPath` | `/srv/content` | Where the content volume is mounted. A relative value resolves against the app's content root (`/app`). |
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
