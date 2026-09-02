namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Configures the startup warmup building block. Add one or more phases; readiness stays
/// Unhealthy until all non-optional phases complete.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/>, <see cref="TimeoutSeconds"/> and <see cref="Retry"/> bind from
/// configuration — see the <c>AddWarmup(IServiceCollection, IConfiguration, Action{WarmupOptions})</c>
/// overload. <see cref="Phases"/> is deliberately code-only: a phase is a delegate, not a config value.
/// </remarks>
public sealed class WarmupOptions
{
    /// <summary>
    /// When <see langword="false"/>, warmup is marked complete immediately (readiness passes
    /// without running any phase). Useful for local development, and as a kill switch — the rest
    /// of this configuration is not validated while warmup is off. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Time budget in seconds shared by all phases of a single attempt. Defaults to 60, and must
    /// be between 1 and 3600 — warmup needing longer than a minute or two is usually work that
    /// belongs somewhere other than a readiness gate, and the ceiling catches a transposed digit
    /// that would otherwise hold readiness at 503 for weeks in silence.
    /// </summary>
    /// <remarks>
    /// This bounds one attempt, not the lifetime of the warmup: an attempt that overruns it is a
    /// failure like any other, and is retried according to <see cref="Retry"/>.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How a failed attempt is retried. Retrying is on by default — see
    /// <see cref="WarmupRetryOptions.MaxAttempts"/> for how to turn it off.
    /// </summary>
    public WarmupRetryOptions Retry { get; } = new();

    /// <summary>The registered phases, run in order.</summary>
    public IList<IWarmupPhase> Phases { get; } = [];

    /// <summary>Adds a phase implementation.</summary>
    public WarmupOptions AddPhase(IWarmupPhase phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        Phases.Add(phase);
        return this;
    }

    /// <summary>
    /// Adds a phase from a delegate.
    /// </summary>
    /// <param name="name">A short name used in logging and warmup state.</param>
    /// <param name="action">The warmup work.</param>
    /// <param name="optional">When <see langword="true"/>, a failure is logged and warmup continues.</param>
    /// <param name="timeoutSeconds">
    /// Optional per-phase budget in seconds. When <see langword="null"/>, the phase is bounded
    /// only by <see cref="TimeoutSeconds"/>. Bounds only work that observes the cancellation
    /// token — see <see cref="IWarmupPhase.TimeoutSeconds"/>.
    /// </param>
    public WarmupOptions AddPhase(
        string name,
        Func<IServiceProvider, CancellationToken, Task> action,
        bool optional = false,
        int? timeoutSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        Phases.Add(new DelegateWarmupPhase(name, optional, timeoutSeconds, action));
        return this;
    }

    private sealed class DelegateWarmupPhase(
        string name,
        bool optional,
        int? timeoutSeconds,
        Func<IServiceProvider, CancellationToken, Task> action) : IWarmupPhase
    {
        public string Name { get; } = name;
        public bool Optional { get; } = optional;
        public int? TimeoutSeconds { get; } = timeoutSeconds;
        public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) => action(services, cancellationToken);
    }
}
