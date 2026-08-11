namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// A single unit of startup warmup work (e.g. open a DB connection pool, compile an ORM
/// model, prime a cache). Phases run in registration order, sharing a single DI scope
/// created for the warmup run.
/// </summary>
public interface IWarmupPhase
{
    /// <summary>A short name used in logging and warmup state.</summary>
    string Name { get; }

    /// <summary>
    /// When <see langword="true"/>, a failure in this phase is logged and warmup continues
    /// (readiness is not failed). When <see langword="false"/>, a failure fails warmup and the
    /// readiness endpoint reports Unhealthy.
    /// </summary>
    bool Optional { get; }

    /// <summary>Runs the phase. <paramref name="services"/> is a scoped provider shared by all phases in the warmup run.</summary>
    Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);
}
