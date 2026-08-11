namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>The lifecycle state of the startup warmup.</summary>
public enum WarmupStatus
{
    /// <summary>Warmup has not completed yet. The readiness endpoint reports Unhealthy.</summary>
    Pending,

    /// <summary>Warmup completed successfully. The readiness endpoint reports Healthy.</summary>
    Healthy,

    /// <summary>A non-optional warmup phase failed. The readiness endpoint reports Unhealthy.</summary>
    Failed
}
