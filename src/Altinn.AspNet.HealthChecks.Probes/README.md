# Altinn.AspNet.HealthChecks.Probes

> **Experimental — pre-1.0.0.** APIs and conventions may change without notice before the
> 1.0.0 release.

Config-driven outbound HTTP probes for
[`Altinn.AspNet.HealthChecks`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks).
Probe a list of upstream services — as absolute URLs, or as paths resolved against a
per-environment base URI — and have them surface on `/health/deep` as hard or soft dependencies.

```bash
dotnet add package Altinn.AspNet.HealthChecks.Probes
```

## Usage

```csharp
using Altinn.AspNet.HealthChecks.Probes;

builder.Services.AddAltinnHealthChecks()
    .AddOutboundProbes(builder.Configuration.GetSection("HealthProbes"), probes =>
    {
        probes.BaseUri = new Uri("https://platform.tt02.altinn.no/");
        probes.Timeout = TimeSpan.FromSeconds(10);
    })
    // Upstreams that are not configuration-driven:
    .AddOutboundProbe("Maskinporten", new Uri("https://test.maskinporten.no/.well-known/oauth-authorization-server"));
```

```json
{
  "HealthProbes": [
    { "Name": "Access Management", "RelativePath": "accessmanagement/api/v1/meta/info", "Hard": true },
    { "Name": "PDP", "Url": "https://pdp.example.no/health" }
  ]
}
```

`RelativePath` resolves against `BaseUri`, so the same configuration works in every environment
with only the base URI changing. `Url` is absolute. **Exactly one of the two must be set** — the
package validates this at registration and names the offending configuration path.

`RelativePath` must be genuinely relative: absolute URIs and leading slashes are rejected rather
than resolved. Both would otherwise pass silently while ignoring `BaseUri` — an absolute value
lets a test deployment probe production, and a leading slash discards any path on the base URI —
and both look correct in a config review.

You pass the `IConfigurationSection` yourself; the package never invents a key name. An Altinn
app's configuration may arrive from Azure App Configuration, so a section that appears in no
`appsettings.json` is not evidence that the key is unset.

## Hard is not the same as critical

| | Tag | Failing means | Effect |
|---|---|---|---|
| `Hard = true` | `external` | `Unhealthy` | `/health/deep` fails |
| `Hard = false` | `external` | `Degraded` | `/health/deep` stays 200 |
| `critical` tag | `critical` | `Unhealthy` | **readiness fails — the instance is de-pooled** |

These are different axes and conflating them is the mistake this package exists to prevent.
`Hard` describes *how loudly a deep probe complains*; `critical` describes *whether your instance
should be taken out of rotation*. Outbound probes are tagged `external` and should essentially
never be `critical` — otherwise an upstream outage pulls your own healthy pods out of the load
balancer, turning someone else's incident into yours.

## Behaviour

- Every probe is tagged `external`, so it runs only on `/health/deep`. Add more via
  `options.Tags`.
- Duplicate names throw at registration, naming the configuration path that introduced the
  collision — including collisions with checks from other packages (`self`, `warmup`) or your own
  `AddCheck` calls. Health check names must be unique; a duplicate that reaches
  `MapAltinnHealthChecks` is a hard startup crash naming only the check, which is much harder to
  trace back. Checks registered *after* this call are still only caught at mapping.
- `Timeout` (default 10s) applies per probe. Options are **per call**, not per chain: a second
  `AddOutboundProbe(...)` chained after `AddOutboundProbes(...)` needs its own `configure`
  callback, or it silently falls back to the defaults.
- An empty or missing section registers nothing and does not throw — an environment may
  legitimately configure no probes.

`BaseUri` resolution uses `new Uri(baseUri, relativePath)`, so a base URI **without** a trailing
slash drops its last segment: `https://example.no/api` + `meta/info` gives
`https://example.no/meta/info`. Include the trailing slash when the base has a path.
