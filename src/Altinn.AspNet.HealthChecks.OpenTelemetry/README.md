# Altinn.AspNet.HealthChecks.OpenTelemetry

> **Experimental — pre-1.0.0.** APIs may change without notice before the 1.0.0 release.

OpenTelemetry companion package to
[`Altinn.AspNet.HealthChecks`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks):
a span processor that drops the noisy ASP.NET Core server spans produced by health check
probes, so `/health*` traffic does not flood your traces.

## Usage

```csharp
tracerProviderBuilder
    .AddHealthCheckActivityFilter() // suppresses the five default /health* endpoints
    .AddOtlpExporter();             // exporters must be registered after the filter
```

Without arguments, the filter suppresses the five endpoints mapped by
`MapAltinnHealthChecks()`: `/alive`, `/health`, `/health/readiness`,
`/health/startup` and `/health/deep`. Pass your own route suffixes to replace the defaults:

```csharp
tracerProviderBuilder.AddHealthCheckActivityFilter("/internal/health");
```

### If you customise the endpoint paths, share the options object

The defaults above are hardcoded, so moving a path leaves the filter matching the old one —
silently, with no error and no clue beyond a growing trace bill. Pass the same
`HealthCheckEndpointOptions` instance to both, and the two cannot disagree:

```csharp
var healthEndpoints = new HealthCheckEndpointOptions();
healthEndpoints.Deep.Path = "/internal/health/deep";
healthEndpoints.Startup.Disable();

builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddHealthCheckActivityFilter(healthEndpoints)   // suppresses exactly what gets mapped
    .AddOtlpExporter());

app.MapAltinnHealthChecks(healthEndpoints);
```

Disabled endpoints are not suppressed: nothing is mapped there, so a route ending that way
belongs to your app and its spans are kept.

Matching is by case-insensitive route **suffix**, so the endpoints stay suppressed when the
app is mounted under a path base. The flip side: any route ending in a suffix is suppressed
too — if your API has a business route like `/api/devices/{id}/health`, its spans are dropped
under the defaults. Pass explicit, more specific suffixes in that case.

## How it works

The filter is a span processor that clears the `Recorded` flag on ASP.NET Core server spans
whose `http.route` (or `url.path`) ends with one of the configured suffixes. Export processors
skip spans that are not recorded, so the span reaches no exporter — but **only exporters added
after the filter**, since processors run in registration order.

The decision is made when the server span ends, so child spans created while the probe ran
(a database call inside a deep check, say) are still exported. To drop the whole trace, filter
at the instrumentation level instead — that also avoids creating the span in the first place:

```csharp
tracerProviderBuilder.AddAspNetCoreInstrumentation(o =>
    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"));
```

## Target frameworks

`net8.0`, `net9.0`, `net10.0`.
