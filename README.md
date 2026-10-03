# zen-velixir-run

Minimal .NET 10 Blazor Server app on the [velixir](https://zen.velixir.run/) free tier (0.25 vCPU, 256 MB).

- No third-party packages, CSS or JS.
- Component styles in `*.razor.css`; globals in `wwwroot/app.css`.
- Binds to `PORT` when set; honors `X-Forwarded-For`/`X-Forwarded-Proto` from the edge.
- Workstation non-concurrent GC and invariant globalization for the memory cap.
- Push to `main` deploys via `.github/workflows/velixir.yml` (secret `VELIXIR_API_KEY`).

```
dotnet run --project Zen.Blazor
./export.sh
```
