# altinn-aspnet-healthchecks

> **Experimental — pre-1.0.0.** This project is unreleased and under active development.
> APIs, conventions, and package layout may change without notice before the 1.0.0 release.

Reusable ASP.NET Core health check endpoints for Digdir/Altinn products, extracted and
generalized from [Dialogporten](https://github.com/altinn/dialogporten): an opinionated
endpoint/tag convention on top of ASP.NET Core health checks, harmonizing the health surface
across Altinn products. Ships as the zero-dependency NuGet package
**`Altinn.AspNet.HealthChecks`**, plus optional companion packages
**`Altinn.AspNet.HealthChecks.Warmup`** (readiness-gating startup warmup) and
**`Altinn.AspNet.HealthChecks.OpenTelemetry`** (suppress health check trace spans).
Concrete checks come from the
[`AspNetCore.HealthChecks.*` packages](https://github.com/xabaril/aspnetcore.diagnostics.healthchecks)
or your own registrations.

See the [package README](src/Altinn.AspNet.HealthChecks/README.md) for usage.

## Layout

| Path | What |
|------|------|
| `src/Altinn.AspNet.HealthChecks` | The core convention (multi-targets net8.0/net9.0/net10.0). Zero NuGet dependencies. |
| `src/Altinn.AspNet.HealthChecks.Warmup` | Companion package: startup warmup phases gating the readiness endpoint. |
| `src/Altinn.AspNet.HealthChecks.OpenTelemetry` | Companion package: OTEL span processor that suppresses health check trace spans. |
| `samples/SampleApi` | Minimal API demonstrating all five endpoints + opt-in warmup. |
| `tests/Altinn.AspNet.HealthChecks.Tests` | xUnit unit + TestServer integration tests. |

## Develop

```bash
dotnet build -c Release
dotnet test  -c Release

# Run the sample and probe the endpoints
ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet run --project samples/SampleApi
curl http://127.0.0.1:5199/health/deep

# Optional: point the sample at a real Postgres to see the factory-based NpgSql check
# (without it, a fake in-memory "database" check is registered instead)
ConnectionStrings__Db="Host=...;Database=...;Username=..." dotnet run --project samples/SampleApi
```

## Adding a companion package

Optional integrations ship as separate packages so the core stays dependency-free. To add one
(say `Altinn.AspNet.HealthChecks.Npgsql`):

1. Create `src/Altinn.AspNet.HealthChecks.Npgsql/` with a csproj declaring only its
   `Description` and `PackageReference`s, plus a `README.md`. Everything else
   (target frameworks, root namespace, versioning, package metadata, README packing) comes
   from `src/Directory.Build.props` and the repo root `Directory.Build.props`; `PackageId`
   and `AssemblyName` default to the project file name.
2. Add the project to `Altinn.AspNet.HealthChecks.slnx`.
3. Done — the publish workflow packs every packable project in the solution.

## Release

Pushing a tag `v<semver>` (e.g. `v0.1.0`) builds, tests, packs, and pushes all packages +
symbols to nuget.org (see `.github/workflows/publish-nuget.yml`). The tag drives the package
version.
