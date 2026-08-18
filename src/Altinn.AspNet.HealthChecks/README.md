# Altinn.AspNet.HealthChecks

> **Experimental — pre-1.0.0.** APIs and conventions may change without notice before the
> 1.0.0 release.

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
    // failureStatus: Degraded = soft dependency (deep endpoint stays 200). For a list of these
    // driven from configuration, use the Altinn.AspNet.HealthChecks.Probes companion package.
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

Health check names must be unique across the app — a duplicate makes `MapAltinnHealthChecks()`
throw at startup. If your app already registers a check called `self`, rename ours rather than
yours:

```csharp
builder.Services.AddAltinnHealthChecks(o => o.SelfCheckName = "process-self");
```

### Customising the endpoints

Each endpoint is an object with a `Path` and optional route conventions. Setting `Path` to
`null` or blank — or calling `Disable()` — leaves it unmapped. (Blank counts because
configuration binders can produce `""` where they cannot produce `null`, and `MapHealthChecks("")`
would otherwise serve the health payload from `/`.)

```csharp
app.MapAltinnHealthChecks(o =>
{
    o.Deep.Path = "/internal/health/deep";
    o.Deep.Configure = endpoint => endpoint.RequireHost("localhost");  // or RequireAuthorization()
    o.Startup.Disable();                                               // platform probes readiness only
});
```

`/health/startup` and `/health` filter on the same tag (`dependencies`) and therefore return the
same content. Point a platform startup probe at `/health/startup` and humans at `/health`; the
split exists so you can move or disable one without disturbing the other.

### Exception details on public endpoints

`/health/deep` includes each failing entry's exception message by default, matching the
HealthChecks UI format byte for byte. Those messages routinely carry connection strings,
hostnames and credentials.

```csharp
app.MapAltinnHealthChecks(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());
```

Turning it off omits the `exception` field **and** the description whenever an exception is
present — when a check throws, the health check service uses the exception message as the entry's
description, so suppressing only the one field would still leak it. The body then no longer
matches the UI format. A future major version will default this to off.

### Config-driven outbound probes

Use the [`Probes`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks.Probes) companion
package, which handles binding, base-URI-relative paths, hard/soft mapping, timeouts and
duplicate-name detection:

```csharp
builder.Services.AddAltinnHealthChecks()
    .AddOutboundProbes(builder.Configuration.GetSection("HealthProbes"),
        probes => probes.BaseUri = new Uri("https://platform.tt02.altinn.no/"));
```

## Companion packages

The core stays dependency-free; optional integrations ship separately:

| Package | What |
|---------|------|
| [`Altinn.AspNet.HealthChecks.Probes`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks.Probes) | Config-driven outbound HTTP probes, absolute or resolved against a per-environment base URI, as hard or soft dependencies. |
| [`Altinn.AspNet.HealthChecks.Warmup`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks.Warmup) | Startup warmup: run ordered warmup phases and keep `/health/readiness` at 503 until they complete. |
| [`Altinn.AspNet.HealthChecks.OpenTelemetry`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks.OpenTelemetry) | Span processor (`AddHealthCheckActivityFilter()`) that keeps health probe spans out of your traces. |

## Target frameworks

`net8.0`, `net9.0`, `net10.0`.
