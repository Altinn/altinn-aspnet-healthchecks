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
{
  "Warmup": {
    "Enabled": true,
    "TimeoutSeconds": 60,
    "Retry": { "MaxAttempts": 0, "InitialDelaySeconds": 2, "MaxDelaySeconds": 60 }
  }
}
```

`Enabled`, `TimeoutSeconds` and `Retry` bind from the section you pass; phases are always code. You supply the
section yourself — the package never invents a key name, because an Altinn app's configuration
may come from Azure App Configuration, where a section absent from every `appsettings.json` is
not evidence the key is unset. There is also an `AddWarmup(warmup => …)` overload with no
configuration.

Phases run in order on startup, sharing a single DI scope created for the attempt.
Non-optional phase failure → readiness `Unhealthy`, and the attempt is retried; `optional: true`
phases log-and-continue and never cause a retry.

## Retrying

A failed attempt is retried by default, for as long as the host runs, with exponential backoff
from `InitialDelaySeconds` up to `MaxDelaySeconds`, jittered. Readiness reports `Unhealthy`
throughout, which is the point: the instance stays out of traffic until it is genuinely warm.

This is not a nicety. The failures warmup hits are usually transient — a DNS hiccup, a database a
second from accepting connections — and they tend to hit every instance a deploy creates at once.
A failed readiness probe is not a restart signal to Kubernetes or Container Apps: the instance is
taken out of load balancing and left running, and it still counts toward `minReplicas`, so nothing
replaces it either. Without a retry, a blip lasting seconds costs you that instance for as long as
it lives.

The backoff is jittered (half fixed, half random) for the same reason: unjittered, every instance
of the deploy would retry in lockstep against whatever is still recovering.

Retries re-run **the whole phase set** from the start, in a new scope — a phase may depend on one
before it, and the scope they shared is gone. So **phases must be idempotent**. The new scope is
deliberate: whatever the failed attempt left faulted in the old one must not be handed to the retry.

`MaxAttempts` counts attempts in total, including the first. `0` (the default) means retry
indefinitely; `1` disables retrying and restores single-shot behaviour.

`TimeoutSeconds` (default 60) is the budget for **one attempt**. A phase-level
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

## Reading the state

`WarmupState.GetSnapshot()` returns the status, the current phase, the attempt number, and the
phase and exception of the last failure. A `Pending` snapshot past attempt 1 is a retry in flight,
and it keeps the previous failure so the readiness endpoint can still say what went wrong.

## How it plugs in

`AddWarmup` registers a hosted service that runs the phases and retries a failed attempt, and a
health check tagged `warmup` — the tag `MapAltinnHealthChecks()` routes onto the readiness
endpoint. It works with any `MapHealthChecks` setup whose readiness predicate includes the
`warmup` tag.

The health check is a pure read of the warmup state: probing it never starts warmup work, so probe
frequency has no bearing on how often the phases run. The hosted service is the only thing that
runs them, and only one attempt is ever in flight.

## Target frameworks

`net8.0`, `net9.0`, `net10.0`.
