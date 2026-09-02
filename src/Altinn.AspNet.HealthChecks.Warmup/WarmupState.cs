namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Thread-safe singleton holding the current <see cref="WarmupStatus"/>. Written by the
/// warmup hosted service and read by <see cref="WarmupHealthCheck"/>.
/// </summary>
/// <remarks>
/// State is held as a single immutable <see cref="WarmupSnapshot"/> that writers replace under
/// a lock and publish with a volatile write, so a reader never sees a half-applied transition
/// (a <see cref="WarmupStatus.Failed"/> status without its phase and exception, say) and never
/// needs the lock itself. Readers take one atomic snapshot via <see cref="GetSnapshot"/>.
/// </remarks>
public sealed class WarmupState
{
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    private WarmupSnapshot _snapshot = WarmupSnapshot.Pending;

    /// <summary>Returns an atomic, self-consistent view of the current warmup state.</summary>
    public WarmupSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    /// <summary>Records that an attempt has started, counting from 1.</summary>
    /// <remarks>
    /// A retry moves the status back to <see cref="WarmupStatus.Pending"/> — both statuses report
    /// Unhealthy, so readiness does not flap — while deliberately keeping the previous attempt's
    /// failed phase and exception. Losing them here would leave a retrying instance unable to say
    /// anything about why it is not ready.
    /// </remarks>
    public void MarkAttemptStarted(int attempt)
    {
        lock (_lock)
        {
            Publish(_snapshot with
            {
                Status = WarmupStatus.Pending,
                CurrentPhase = null,
                Attempt = attempt
            });
        }
    }

    /// <summary>Records that a phase has started running.</summary>
    public void MarkPhaseStarted(string phase)
    {
        lock (_lock)
        {
            Publish(_snapshot with { CurrentPhase = phase });
        }
    }

    /// <summary>Records that warmup completed successfully.</summary>
    public void MarkWarmupComplete()
    {
        lock (_lock)
        {
            Publish(_snapshot with
            {
                Status = WarmupStatus.Healthy,
                CurrentPhase = null,
                FailedPhase = null,
                Exception = null
            });
        }
    }

    /// <summary>Records that warmup failed in the given phase.</summary>
    public void MarkWarmupFailed(string phase, Exception ex)
    {
        lock (_lock)
        {
            Publish(_snapshot with
            {
                Status = WarmupStatus.Failed,
                CurrentPhase = null,
                FailedPhase = phase,
                Exception = ex
            });
        }
    }

    // Publishing with a volatile write pairs with the volatile read in GetSnapshot: readers
    // see either the whole previous snapshot or the whole new one.
    private void Publish(WarmupSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}
