# zen-velixir-run

Minimal .NET 10 Blazor Server app on the [velixir](https://zen.velixir.run/) free tier (0.25 vCPU, 256 MB).

- Live message board: anyone posts up to 500 chars, everyone connected sees it instantly. In memory only, newest 50 kept, 2 s per-visitor cooldown.
- Header shows server time, ticking every second from one shared server timer.
- Beacons (`/beacons`): public, unauthenticated capture API with a live page and usage docs. See below.
- Packages: Dapper and Npgsql only. No CSS or JS frameworks. Evergreen browsers only.
- Component styles in `*.razor.css`; globals and color tokens (`light-dark()`) in `wwwroot/app.css`.
- Binds to `PORT` when set; honors `X-Forwarded-For`/`X-Forwarded-Proto` from the edge.
- Memory cap: workstation non-concurrent GC, `ConserveMemory=7`, no tiered PGO, invariant globalization, short disconnected-circuit retention, 2 unacked render batches per circuit.
- Data protection keys are in-memory; a restart invalidates antiforgery cookies, so that log category is off. Restarts show as `Application started` in logs.
- Messages truncate on grapheme boundaries.
- Every page sends `Content-Security-Policy-Report-Only`; reports land in beacon key `zen-csp`.
- Push to `main` builds then deploys via `.github/workflows/velixir.yml` (secret `VELIXIR_API_KEY`). PRs build only.

## Beacons

| Method | Path | Result |
|---|---|---|
| POST | `/api/beacons/{key}` | 201, UUIDv7 id, `Location` |
| GET | `/api/entries/{id}` | Full record or 404; 503 + `Retry-After` if DB down and not in memory |
| GET | `/api/beacons/{key}?referer=` | Total, bytes, first/last, top 25 referers, hourly 24 h |
| GET | `/api/beacons/{key}/entries?referer=&before=&limit=` | Newest first, max 100, `next` cursor |
| GET | `/api/beacons?limit=` | Busiest keys |
| GET | `/api/status` | Storage health |

- Key: `^[A-Za-z0-9][A-Za-z0-9._~:-]{0,63}$`. Body ≤ 32 KiB, any type; invalid UTF-8 or NUL stored as base64.
- Records method, path, query, referer (`Referer` else `Origin`), content type, user agent, headers, body. IP truncated to /24 or /48. `Cookie`, `Authorization` and forwarding headers redacted.
- Open CORS. Per-IP token buckets: 30 writes/min, 120 reads/min.
- Writes go to a bounded memory buffer (4 MiB / 2000 entries) and a background writer batches them into PostgreSQL. DB down: reads serve memory with `degraded: true`, writer retries with backoff (2–60 s), oldest unsaved entries drop if the buffer fills.
- Janitor every 2 min: deletes entries older than 30 days, trims oldest to 80% when stored bytes exceed 48 MiB, halves the budget and vacuums if `pg_database_size` exceeds 160 MiB and is still growing.
- Pool max 2 connections (server allows 5). Schema is created on first use.
- No PostgreSQL settings: memory only.
- velixir blocks outbound ports other than 80/443 unless opened by support ticket; an external database on 5432 needs that. Pods have no IPv6 route. `/api/status` `lastError` shows the socket error.

Settings in `appsettings.json` under `Beacons`. Connection from env vars `postgreshost`, `postgresport`, `postgresdatabase`, `postgresuser`, `postgrespassword`, or `postgresconnectionstring` (`postgresql://user:pass@host:port/db`) as fallback.

```
dotnet run --project Zen.Blazor
./export.sh
```
