namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// An immutable, self-consistent view of <see cref="WarmupState"/> at one point in time.
/// </summary>
/// <param name="Status">The warmup status.</param>
/// <param name="CurrentPhase">The phase running when the snapshot was taken, if any.</param>
/// <param name="FailedPhase">The phase that failed, set when <paramref name="Status"/> is <see cref="WarmupStatus.Failed"/>.</param>
/// <param name="Exception">The exception that failed warmup, set when <paramref name="Status"/> is <see cref="WarmupStatus.Failed"/>.</param>
public sealed record WarmupSnapshot(
    WarmupStatus Status,
    string? CurrentPhase,
    string? FailedPhase,
    Exception? Exception)
{
    /// <summary>The state of a warmup that has not started yet.</summary>
    public static WarmupSnapshot Pending { get; } =
        new(WarmupStatus.Pending, CurrentPhase: null, FailedPhase: null, Exception: null);

    /// <summary>
    /// Which attempt is running, counting from 1, or 0 before the first attempt starts.
    /// </summary>
    /// <remarks>
    /// A <see cref="WarmupStatus.Pending"/> snapshot past attempt 1 is a retry in flight, and it
    /// keeps <see cref="FailedPhase"/> and <see cref="Exception"/> from the attempt before it —
    /// so the readiness endpoint can still say what went wrong while the next attempt runs.
    /// </remarks>
    public int Attempt { get; init; }

    /// <summary>Whether warmup has completed successfully.</summary>
    public bool IsWarmupComplete => Status == WarmupStatus.Healthy;
}
