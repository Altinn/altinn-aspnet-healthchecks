# Altinn.AspNet.HealthChecks.Warmup

> **Experimental — pre-1.0.0.** APIs may change without notice before the 1.0.0 release.

Startup warmup companion package to
[`Altinn.AspNet.HealthChecks`](https://www.nuget.org/packages/Altinn.AspNet.HealthChecks):
keeps `/health/readiness` returning 503 until your app has warmed up (JIT the ORM model, open
the DB pool, prime caches), so an instance is not sent traffic before it is ready — without
failing liveness and getting killed.

## Usage

```csharp
using Altinn.AspNet.HealthChecks.Warmup;

builder.Services.AddWarmup(builder.Configuration.GetSection("Warmup"), warmup =>
{
    warmup.AddPhase("db-pool", async (sp, ct) =>
    {
        await using var conn = await sp.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);
        // ...
    },
    // Optional per-phase budget, so one slow phase cannot consume the whole run.
    timeoutSeconds: 15);

    warmup.AddPhase("prime-search", (sp, ct) => Prime(sp, ct), optional: true); // failure won't fail readiness
});
```

```json
{ "Warmup": { "Enabled": true, "TimeoutSeconds": 60 } }
```

`Enabled` and `TimeoutSeconds` bind from the section you pass; phases are always code. You supply the
section yourself — the package never invents a key name, because an Altinn app's configuration
may come from Azure App Configuration, where a section absent from every `appsettings.json` is
not evidence the key is unset. There is also an `AddWarmup(warmup => …)` overload with no
configuration.

Phases run in order on startup, sharing a single DI scope created for the warmup run.
Non-optional phase failure → readiness `Unhealthy`; `optional: true` phases log-and-continue.

`TimeoutSeconds` (default 60) is the budget for the **whole run**. A phase-level
`timeoutSeconds:` is layered underneath it: without one, a slow optional phase can eat the entire
run budget and starve a later required phase, which then fails readiness while naming the wrong
phase. Both must be between 1 and 3600, validated at host startup — a bad value is a clear boot
failure rather than readiness silently stuck `Pending`, and the ceiling catches the transposed
digit that would otherwise hold readiness at 503 for weeks in silence.

Both timeouts work by cancelling the token passed to the phase, so they bound only work that
observes cancellation. A phase that blocks without checking its token cannot be interrupted by
any timeout here and will hold readiness at `Pending` until it returns — plumb the token through
to whatever the phase actually calls.

`Enabled: false` is a genuine kill switch: warmup completes immediately, and the rest of this
configuration is not validated, so a bad timeout cannot block startup on a subsystem that is
switched off.

## How it plugs in

`AddWarmup` registers a hosted service that runs the phases, and a health check tagged
`warmup` — the tag `MapAltinnHealthChecks()` routes onto the readiness endpoint. It works
with any `MapHealthChecks` setup whose readiness predicate includes the `warmup` tag.

## Target frameworks

`net8.0`, `net9.0`, `net10.0`.
