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
| `/alive`             | `live`                            | Liveness (process only)   |
| `/health/readiness`  | `critical` or `warmup`            | Readiness / de-pooling    |
| `/health/startup`    | `dependencies`                    | Startup                   |
| `/health`            | `dependencies`                    | Dashboard / humans        |
| `/health/deep`       | `dependencies` or `external`      | Deep probe (outbound)     |

`/alive` and `/health` are the paths the Microsoft/Aspire service-defaults scaffolding uses, so
these are the endpoints an Altinn Kubernetes deployment already probes. The tag on the liveness
endpoint is `live` for the same reason: a check registered as `AddCheck("self", …, ["live"])`
surfaces there without retagging. Every path is overridable — see
[Customising the endpoints](#customising-the-endpoints).

## Response format

Endpoints answer `application/vnd.altinn.health.v1+json`:

```json
{
  "status": "healthy",
  "totalDuration": "00:00:00.0412000",
  "entries": {
    "postgres": {
      "status": "healthy",
      "duration": "00:00:00.0070000",
      "description": "up",
      "data": { "pool": "primary" },
      "tags": ["dependencies", "critical"]
    }
  }
}
```

A vendor media type, so the shape can be versioned independently of the package. A client asking
for plain `application/json` gets this too — a `+json` type is a subset of it — so nothing needs
to know the vendor type exists. Everything past `status` and `duration` is omitted when absent or
withheld: see [Detail levels](#detail-levels).

Ask for `text/plain` instead and the body is the overall status as one lowercase word, which is
all a human wants from a `curl`:

```console
$ curl -s -H 'Accept: text/plain' localhost:5199/health
healthy
```

Negotiation follows the `Accept` header's quality values. A request that expresses no usable
preference — no header at all, `*/*`, or nothing on offer — gets JSON. Nothing ever returns 406:
the status code already carries the health signal, and replacing a 503 with a 406 would throw away
the only thing the caller came for. Add your own format by putting a `HealthReportFormatter` on
`Formatters`.

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

Use the constants in `HealthCheckTags` (`Live`, `Dependencies`, `Critical`, `Warmup`,
`External`) to decide where each check appears. Follow the **severity-by-consequence** rule:
return `Unhealthy` only when restarting/de-pooling the instance helps; return `Degraded` for
dependencies you can tolerate (cache miss, buffered outbox, optional lookups).

Health check names must be unique across the app — a duplicate makes `MapAltinnHealthChecks()`
throw at startup. If your app already registers a check called `self`, rename ours rather than
yours:

```csharp
builder.Services.AddAltinnHealthChecks(o => o.SelfCheckName = "process-self");
```

If you are writing a *library* that registers a check for shared infrastructure, claim the name
instead of registering it outright, so two libraries wiring up the same dependency cannot fail the
app's startup:

```csharp
builder.Services.TryAddHealthCheck("PostgreSql", checks => checks.AddNpgSql(
    sp => sp.GetRequiredService<NpgsqlDataSource>(),
    name: "PostgreSql",
    tags: [HealthCheckTags.Dependencies, HealthCheckTags.Critical]));
```

First registration wins, and the callback is skipped entirely if the name is taken. Only names
claimed through `TryAddHealthCheck` are tracked — a check added directly to `AddHealthChecks()`
will still collide.

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

### Detail levels

Health endpoints leak. Exception messages routinely carry connection strings, hostnames and
credentials — and when a check *throws*, the health check service uses the exception message as
the entry's description, so suppressing only the `exception` field would publish it anyway. A
check's `data` is whatever that check felt like publishing: MassTransit's bus-state check reports
the broker host address and every queue name it knows, while perfectly healthy.

So how much of a report reaches the body is one dial, `DetailLevel`:

| Level        | status, durations | `tags` | `description`            | `data`         | `exception.message` | `stackTrace`, `innerException` |
|--------------|:-----------------:|:------:|--------------------------|:--------------:|:-------------------:|:------------------------------:|
| `Minimal`    | ✔                 |        |                          |                |                     |                                |
| `Summary`    | ✔                 | ✔      | only if no exception     |                |                     |                                |
| `Diagnostic` | ✔                 | ✔      | ✔                        | ✔ if non-empty | ✔                   |                                |
| `Full`       | ✔                 | ✔      | ✔                        | ✔ if non-empty | ✔                   | ✔                              |

Left unset — the default — it follows `IHostEnvironment`:

| Environment                    | Level        |
|--------------------------------|--------------|
| `Development`                  | `Full`       |
| `Production`                   | `Summary`    |
| anything else (`Staging`, `at22`, …) | `Diagnostic` |

That is usually the whole configuration: a production body carries no secrets without the app
having to say so, and development still shows stack traces. Override it to loosen an endpoint that
is not publicly reachable:

```csharp
app.MapAltinnHealthChecks(o =>
{
    o.Deep.Configure = endpoint => endpoint.RequireHost("localhost");
    o.DetailLevel = HealthReportDetailLevel.Full;
});
```

The level applies to every mapped endpoint. If you need one endpoint more revealing than the rest,
map it yourself with its own `HealthReportResponseWriter`.

### HTTP metrics

Kubernetes probes these endpoints every few seconds forever, which is enough to dominate
`http.server.request.duration` without saying anything about how the app serves real traffic. So
the mapped endpoints are excluded from HTTP metrics by default; set `DisableHttpMetrics = false` to
keep them. (No effect on `net8.0`, where the underlying `DisableHttpMetrics()` does not exist.)

Trace spans are the other half, and are suppressed separately by the
[`OpenTelemetry`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks.OpenTelemetry)
companion package.

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
