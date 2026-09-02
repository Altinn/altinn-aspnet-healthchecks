namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// How a failed warmup is retried. Binds from the same configuration section as the rest of
/// <see cref="WarmupOptions"/>, under a <c>Retry</c> subsection.
/// </summary>
/// <remarks>
/// The trigger for a failed warmup is usually transient — a DNS hiccup, a broker still starting,
/// a database a second away from accepting connections — and it tends to hit every instance
/// created by the same deploy at once. Without a retry, such a blip turns into an instance that
/// is unready for as long as it lives: readiness failing is not a restart signal to Kubernetes or
/// Container Apps, so nothing replaces it either.
/// </remarks>
public sealed class WarmupRetryOptions
{
    /// <summary>
    /// The number of attempts to make in total, including the first. Defaults to 0, meaning
    /// retry for as long as the host runs. Set it to 1 to disable retrying.
    /// </summary>
    /// <remarks>
    /// Retrying indefinitely is the useful default: readiness reports Unhealthy either way, so an
    /// instance that cannot warm up is kept out of traffic whether or not it keeps trying, and
    /// giving up only removes the chance of recovering without human intervention.
    /// </remarks>
    public int MaxAttempts { get; set; }

    /// <summary>
    /// The backoff before the second attempt, in seconds. Each further attempt doubles it, up to
    /// <see cref="MaxDelaySeconds"/>. Defaults to 2.
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 2;

    /// <summary>
    /// The ceiling on the backoff, in seconds. Defaults to 60. Must be at least
    /// <see cref="InitialDelaySeconds"/>.
    /// </summary>
    public int MaxDelaySeconds { get; set; } = 60;
}
