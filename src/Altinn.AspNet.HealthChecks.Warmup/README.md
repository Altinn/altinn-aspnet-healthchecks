# Altinn.AspNet.HealthChecks.Warmup

> **Experimental — pre-1.0.0.** This package is unreleased and under active development.
> APIs may change without notice before the 1.0.0 release.

Startup warmup companion package to
[`Altinn.AspNet.HealthChecks`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks):
keeps `/health/readiness` returning 503 until your app has warmed up (JIT the ORM model, open
the DB pool, prime caches), so an instance is not sent traffic before it is ready — without
failing liveness and getting killed.

## Usage

```csharp
using Altinn.AspNet.HealthChecks.Warmup;

builder.Services.AddWarmup(warmup =>
{
    warmup.TimeoutSeconds = 60;
    warmup.AddPhase("db-pool", async (sp, ct) =>
    {
        await using var conn = await sp.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);
        // ...
    });
    warmup.AddPhase("prime-search", (sp, ct) => Prime(sp, ct), optional: true); // failure won't fail readiness
});
```

Phases run in order on startup, sharing a single DI scope created for the warmup run.
Non-optional phase failure → readiness `Unhealthy`; `optional: true` phases log-and-continue.

## How it plugs in

`AddWarmup` registers a hosted service that runs the phases, and a health check tagged
`warmup` — the tag `MapAltinnHealthChecks()` routes onto the readiness endpoint. It works
with any `MapHealthChecks` setup whose readiness predicate includes the `warmup` tag.

## Target frameworks

`net8.0`, `net9.0`, `net10.0`.
