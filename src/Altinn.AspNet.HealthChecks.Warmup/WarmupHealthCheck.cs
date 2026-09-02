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
        var snapshot = _warmupState.GetSnapshot();

        var result = snapshot.Status switch
        {
            WarmupStatus.Healthy => HealthCheckResult.Healthy("Readiness warmup completed."),
            WarmupStatus.Failed => HealthCheckResult.Unhealthy(
                $"Readiness warmup failed in phase '{snapshot.FailedPhase ?? "unknown"}'{Attempt(snapshot)}.",
                snapshot.Exception),
            // No exception attached, even on a retry that has one to hand: a health report entry
            // carrying an exception has its description withheld at anything below Diagnostic
            // detail, and which phase is retrying is worth more to a first responder than an
            // exception nobody outside development will be shown. It is in the logs either way.
            WarmupStatus.Pending => HealthCheckResult.Unhealthy(
                $"Readiness warmup is pending in phase '{snapshot.CurrentPhase ?? "not-started"}'{Attempt(snapshot)}{PreviousFailure(snapshot)}."),
            _ => throw new InvalidOperationException($"Unknown warmup status '{snapshot.Status}'.")
        };

        return Task.FromResult(result);
    }

    // Suppressed before the first attempt is recorded, so a state written directly (a test, or a
    // host that never started the warmup service) does not read "attempt 0".
    private static string Attempt(WarmupSnapshot snapshot) =>
        snapshot.Attempt > 0 ? $" (attempt {snapshot.Attempt})" : string.Empty;

    private static string PreviousFailure(WarmupSnapshot snapshot) =>
        snapshot.FailedPhase is { } phase ? $"; the previous attempt failed in phase '{phase}'" : string.Empty;
}
