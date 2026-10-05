# zen-velixir-run

Minimal .NET 10 Blazor Server app on the [velixir](https://zen.velixir.run/) free tier (0.25 vCPU, 256 MB).

- Live message board: anyone posts up to 500 chars, everyone connected sees it instantly. In memory only, newest 50 kept, 2 s per-visitor cooldown.
- Header shows server time, ticking every second from one shared server timer.
- No third-party packages, CSS or JS. Evergreen browsers only.
- Component styles in `*.razor.css`; globals and color tokens (`light-dark()`) in `wwwroot/app.css`.
- Binds to `PORT` when set; honors `X-Forwarded-For`/`X-Forwarded-Proto` from the edge.
- Memory cap: workstation non-concurrent GC, `ConserveMemory=7`, no tiered PGO, invariant globalization, short disconnected-circuit retention, 2 unacked render batches per circuit.
- Data protection keys are in-memory; a restart invalidates antiforgery cookies, so that log category is off. Restarts show as `Application started` in logs.
- Messages truncate on grapheme boundaries.
- Push to `main` deploys via `.github/workflows/velixir.yml` (secret `VELIXIR_API_KEY`).

```
dotnet run --project Zen.Blazor
./export.sh
```
