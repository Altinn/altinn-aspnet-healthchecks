namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>The lifecycle state of the startup warmup.</summary>
public enum WarmupStatus
{
    /// <summary>
    /// Warmup has not completed yet — either the first attempt is still running, or a retry is.
    /// The readiness endpoint reports Unhealthy.
    /// </summary>
    Pending,

    /// <summary>Warmup completed successfully. The readiness endpoint reports Healthy.</summary>
    Healthy,

    /// <summary>
    /// A non-optional warmup phase failed. The readiness endpoint reports Unhealthy. Unless
    /// retrying is switched off or its attempts are spent, this is the gap between one attempt
    /// and the next rather than a final state — see <see cref="WarmupRetryOptions"/>.
    /// </summary>
    Failed
}
