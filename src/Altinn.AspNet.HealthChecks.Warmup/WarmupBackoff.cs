namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// The delay between warmup attempts: exponential backoff, capped, with jitter.
/// </summary>
internal static class WarmupBackoff
{
    // 2^30 initial delays is already past the cap for any legal configuration; going further
    // only risks overflowing the exponent for a host that has been retrying for a very long time.
    private const int MaxExponent = 30;

    /// <summary>
    /// The delay to wait after <paramref name="failedAttempts"/> failures.
    /// </summary>
    /// <param name="failedAttempts">How many attempts have failed so far, at least 1.</param>
    /// <param name="retry">The configured backoff bounds.</param>
    /// <param name="jitterSample">A sample in [0, 1), supplied by the caller so the curve stays testable.</param>
    /// <remarks>
    /// Jitter is not decoration here. The failures this retries are typically infrastructure-wide
    /// and hit every instance of a deploy within the same second, so an unjittered backoff would
    /// have all of them retry in lockstep and hammer whatever is recovering. Half the delay is
    /// fixed and half is random ("equal jitter"), which spreads the attempts out while keeping a
    /// floor under the retry rate.
    /// </remarks>
    internal static TimeSpan ComputeDelay(int failedAttempts, WarmupRetryOptions retry, double jitterSample)
    {
        ArgumentNullException.ThrowIfNull(retry);

        var exponent = Math.Clamp(failedAttempts - 1, 0, MaxExponent);
        var seconds = Math.Min(retry.InitialDelaySeconds * Math.Pow(2, exponent), retry.MaxDelaySeconds);
        var half = seconds / 2;

        return TimeSpan.FromSeconds(half + (half * jitterSample));
    }
}
