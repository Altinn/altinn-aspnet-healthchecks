# Altinn.AspNet.HealthChecks

> **Experimental — pre-1.0.0.** This package is unreleased and under active development.
> APIs and conventions may change without notice before the 1.0.0 release.

A declarative, opinionated health check endpoint **convention** for ASP.NET Core, extracted
and generalized from [Altinn Dialogporten](https://github.com/altinn/dialogporten) to
harmonize the health surface across Altinn products. Add the package, call two extension
methods in `Program.cs`, and you get the same endpoint layout that powers
`https://platform.altinn.no/dialogporten/health/deep`.

This package deliberately contains **no health checks of its own** (beyond the trivial
`self` liveness check) and has **zero NuGet dependencies**. It provides the endpoint layout,
the tag-based routing convention, and the standard JSON response format. The checks
themselves come from wherever you like — most commonly the
[`AspNetCore.HealthChecks.*` packages](https://github.com/xabaril/aspnetcore.diagnostics.healthchecks)
(Postgres, Redis, RabbitMQ, outbound URLs, ...) or your own `AddCheck<T>` registrations.

## Endpoints

`MapAltinnHealthChecks()` maps five endpoints. Each filters the registered checks by **tag**,
so you register a check once (with tags) and it surfaces on the right endpoints:

| Path                 | Includes checks tagged            | Intended probe            |
|----------------------|-----------------------------------|---------------------------|
| `/health/liveness`   | `self`                            | Liveness (process only)   |
| `/health/readiness`  | `critical` or `warmup`            | Readiness / de-pooling    |
| `/health/startup`    | `dependencies`                    | Startup                   |
| `/health`            | `dependencies`                    | Dashboard / humans        |
| `/health/deep`       | `dependencies` or `external`      | Deep probe (outbound)     |

All endpoints emit the de-facto standard HealthChecks UI JSON (the format understood by the
[HealthChecks UI](https://github.com/xabaril/aspnetcore.diagnostics.healthchecks) dashboard),
so `/health/deep` is structurally identical to the Dialogporten reference deployment. The
writer is implemented in this package (`HealthCheckJsonResponseWriter`, verified byte-identical
to `AspNetCore.HealthChecks.UI.Client`) — hence the zero dependencies.

## Quick start

Install this package plus the `AspNetCore.HealthChecks.*` packages for the dependencies your
app actually has:

```bash
dotnet add package Altinn.AspNet.HealthChecks
dotnet add package AspNetCore.HealthChecks.NpgSql   # provides AddNpgSql   (used below)
dotnet add package AspNetCore.HealthChecks.Redis    # provides AddRedis    (used below)
dotnet add package AspNetCore.HealthChecks.Uris     # provides AddUrlGroup (used below)
```

```csharp
using Altinn.AspNet.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    // Registers the `self` liveness check and enables the endpoint convention.
    .AddAltinnHealthChecks()
    // Register dependency checks with the standard tags. Prefer the factory overloads:
    // the check then probes the same NpgsqlDataSource / IConnectionMultiplexer the app
    // uses (same pooling, same auth) instead of opening a parallel connection.
    .AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(),
        tags: [HealthCheckTags.Dependencies, HealthCheckTags.Critical])
    .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(),
        tags: [HealthCheckTags.Dependencies])
    // Outbound probes of upstream services, tagged External so they only run on /health/deep.
    // failureStatus: Degraded = soft dependency (deep endpoint stays 200).
    .AddUrlGroup(new Uri("https://maskinporten.no/.well-known/oauth-authorization-server"),
        name: "Maskinporten",
        failureStatus: HealthStatus.Degraded,
        tags: [HealthCheckTags.External]);

var app = builder.Build();
app.MapAltinnHealthChecks();
app.Run();
```

Use the constants in `HealthCheckTags` (`Self`, `Dependencies`, `Critical`, `Warmup`,
`External`) to decide where each check appears. Follow the **severity-by-consequence** rule:
return `Unhealthy` only when restarting/de-pooling the instance helps; return `Degraded` for
dependencies you can tolerate (cache miss, buffered outbox, optional lookups).

### Custom routes

```csharp
app.MapAltinnHealthChecks(routes => routes.DeepPath = "/internal/health/deep");
```

### Config-driven outbound probes

`AddUrlGroup` registrations are code, but nothing stops you from binding the list from
configuration:

```csharp
foreach (var probe in builder.Configuration.GetSection("HealthProbes").Get<List<ProbeConfig>>() ?? [])
{
    healthChecks.AddUrlGroup(new Uri(probe.Url), name: probe.Name,
        failureStatus: probe.Hard ? HealthStatus.Unhealthy : HealthStatus.Degraded,
        tags: [HealthCheckTags.External]);
}
```

## Companion packages

The core stays dependency-free; optional integrations ship separately:

| Package | What |
|---------|------|
| [`Altinn.AspNet.HealthChecks.Warmup`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks.Warmup) | Startup warmup: run ordered warmup phases and keep `/health/readiness` at 503 until they complete. |
| [`Altinn.AspNet.HealthChecks.OpenTelemetry`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks.OpenTelemetry) | Span processor (`AddHealthCheckActivityFilter()`) that keeps health probe spans out of your traces. |

## Target frameworks

`net8.0`, `net9.0`, `net10.0`.
