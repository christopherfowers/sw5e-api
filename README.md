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

## Deployment

The API is designed to run behind a TLS-terminating reverse proxy — Azure App
Service's front end, or nginx in front of the container in a Docker
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

#### Docker / nginx

If nginx runs as a sibling container on the same Docker network, Kestrel
sees nginx's address on that network as the remote IP — typically the
network's gateway or nginx's own container IP, not `127.0.0.1`. Confirm the
actual address with `docker network inspect` on the compose network, then
set `ForwardedHeaders__KnownProxies__0` (a fixed container IP) or
`ForwardedHeaders__KnownNetworks__0` (the network's CIDR range) as an
environment variable on the API container — via `docker run -e`, the
`environment:` block in `docker-compose.yml`, or a mounted
`appsettings.Production.json` with a nested `ForwardedHeaders` object
mirroring the table above.

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
