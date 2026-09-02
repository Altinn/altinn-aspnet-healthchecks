namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// A single unit of startup warmup work (e.g. open a DB connection pool, compile an ORM
/// model, prime a cache). Phases run in registration order, sharing a single DI scope
/// created for the attempt.
/// </summary>
/// <remarks>
/// Phases must be idempotent. A failed attempt is retried by re-running the whole set from the
/// start, in a new scope — including the phases that already succeeded, since a phase may depend
/// on one before it and the scope they shared is gone.
/// </remarks>
public interface IWarmupPhase
{
    /// <summary>A short name used in logging and warmup state.</summary>
    string Name { get; }

    /// <summary>
    /// When <see langword="true"/>, a failure in this phase is logged and warmup continues
    /// (readiness is not failed, and the attempt is not retried). When <see langword="false"/>, a
    /// failure fails the attempt and the readiness endpoint reports Unhealthy.
    /// </summary>
    bool Optional { get; }

    /// <summary>
    /// Per-phase time budget in seconds. <see langword="null"/> means the phase is bounded only
    /// by <see cref="WarmupOptions.TimeoutSeconds"/>, the budget shared by the whole attempt. Set
    /// it on phases that could overrun — without one, a slow optional phase can consume the shared
    /// budget and starve a later required phase, which then fails readiness while naming the
    /// wrong phase.
    /// </summary>
    /// <remarks>
    /// Enforced by cancelling the token passed to <see cref="RunAsync"/>, so it bounds only work
    /// that observes cancellation. A phase that blocks without checking its token cannot be
    /// interrupted by any timeout here, and will hold readiness at Pending until it returns —
    /// plumb the token through to whatever the phase actually calls.
    /// </remarks>
    int? TimeoutSeconds { get; }

    /// <summary>Runs the phase. <paramref name="services"/> is a scoped provider shared by all phases in the attempt.</summary>
    Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);
}
