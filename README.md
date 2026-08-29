# sw5e-api

Backend API for the SW5e community platform.

## Requirements

- .NET SDK 10.0.302 or later
- Docker (for PostgreSQL 17 during integration tests)

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

## Security

Every response carries a restrictive baseline of security headers, applied by
`SecurityHeadersMiddleware` before any other middleware so that error responses
are covered. See [SECURITY.md](SECURITY.md) for reporting instructions.

## License

MIT — see [LICENSE](LICENSE).
