namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Configures the startup warmup building block. Add one or more phases; readiness stays
/// Unhealthy until all non-optional phases complete.
/// </summary>
public sealed class WarmupOptions
{
    /// <summary>
    /// When <see langword="false"/>, warmup is marked complete immediately (readiness passes
    /// without running any phase). Useful for local development. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Overall timeout for all phases combined. Defaults to 60 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;

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
    /// Adds a phase from a delegate. <paramref name="optional"/> phases log-and-continue on
    /// failure; non-optional phases fail readiness.
    /// </summary>
    public WarmupOptions AddPhase(
        string name,
        Func<IServiceProvider, CancellationToken, Task> action,
        bool optional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        Phases.Add(new DelegateWarmupPhase(name, optional, action));
        return this;
    }

    private sealed class DelegateWarmupPhase(
        string name,
        bool optional,
        Func<IServiceProvider, CancellationToken, Task> action) : IWarmupPhase
    {
        public string Name { get; } = name;
        public bool Optional { get; } = optional;
        public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) => action(services, cancellationToken);
    }
}
