using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Reports readiness based on <see cref="WarmupState"/>. Unhealthy while warmup is pending or
/// after a non-optional phase failed; Healthy once warmup completes. Registered on the
/// readiness endpoint via the <see cref="HealthCheckTags.Warmup"/> tag.
/// </summary>
internal sealed class WarmupHealthCheck : IHealthCheck
{
    private readonly WarmupState _warmupState;

    public WarmupHealthCheck(WarmupState warmupState)
    {
        ArgumentNullException.ThrowIfNull(warmupState);
        _warmupState = warmupState;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // One snapshot, so status, phase and exception in the message always agree.
        var snapshot = _warmupState.GetSnapshot();

        var result = snapshot.Status switch
        {
            WarmupStatus.Healthy => HealthCheckResult.Healthy("Readiness warmup completed."),
            WarmupStatus.Failed => HealthCheckResult.Unhealthy(
                $"Readiness warmup failed in phase '{snapshot.FailedPhase ?? "unknown"}'.",
                snapshot.Exception),
            WarmupStatus.Pending => HealthCheckResult.Unhealthy(
                $"Readiness warmup is pending in phase '{snapshot.CurrentPhase ?? "not-started"}'."),
            _ => throw new InvalidOperationException($"Unknown warmup status '{snapshot.Status}'.")
        };

        return Task.FromResult(result);
    }
}
