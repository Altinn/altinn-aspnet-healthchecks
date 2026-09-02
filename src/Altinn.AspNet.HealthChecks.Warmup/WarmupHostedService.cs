using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Runs the configured <see cref="IWarmupPhase"/> set on startup (off the startup path, so it
/// does not block the host from listening) and records progress in <see cref="WarmupState"/>.
/// Retries a failed attempt with backoff, per <see cref="WarmupOptions.Retry"/>.
/// </summary>
/// <remarks>
/// The retry loop is the only thing that ever runs a phase. Probes never trigger one — the
/// readiness health check is a pure read of <see cref="WarmupState"/> — so there is exactly one
/// warmup in flight per process, and probe frequency has no bearing on it.
/// </remarks>
internal sealed partial class WarmupHostedService : BackgroundService
{
    private readonly ILogger<WarmupHostedService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WarmupState _warmupState;
    private readonly TimeProvider _timeProvider;
    private readonly WarmupOptions _options;

    public WarmupHostedService(
        ILogger<WarmupHostedService> logger,
        IServiceScopeFactory scopeFactory,
        WarmupState warmupState,
        TimeProvider timeProvider,
        IOptions<WarmupOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(warmupState);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _scopeFactory = scopeFactory;
        _warmupState = warmupState;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Everything before the first await runs synchronously inside StartAsync, so the Enabled
        // early-out completes before the host starts listening. Timeout validation lives in
        // WarmupOptionsValidator (ValidateOnStart), not here.
        if (!_options.Enabled)
        {
            WarmupDisabled(_logger);
            _warmupState.MarkWarmupComplete();
            return;
        }

        WarmupQueued(_logger);

        // Hop off the startup path so a phase that blocks before its first await cannot
        // stall host startup.
        await Task.Yield();

        await PerformWarmupAsync(stoppingToken);
    }

    private async Task PerformWarmupAsync(CancellationToken stoppingToken)
    {
        var retry = _options.Retry;

        for (var attempt = 1; ; attempt++)
        {
            _warmupState.MarkAttemptStarted(attempt);

            var failure = await RunAttemptAsync(attempt, stoppingToken);

            if (failure is null)
            {
                WarmupCompleted(_logger, attempt);
                _warmupState.MarkWarmupComplete();
                return;
            }

            _warmupState.MarkWarmupFailed(failure.Phase, failure.Exception);

            // The host is going away: nothing to retry into, and the state already records why
            // this attempt ended.
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            // MaxAttempts of 0 means retry for as long as the host runs, so only a positive
            // value can exhaust.
            if (retry.MaxAttempts > 0 && attempt >= retry.MaxAttempts)
            {
                WarmupFailed(_logger, failure.Phase, attempt, failure.Exception);
                return;
            }

            var delay = WarmupBackoff.ComputeDelay(attempt, retry, Random.Shared.NextDouble());
            WarmupAttemptFailed(_logger, attempt, failure.Phase, delay.TotalSeconds, failure.Exception);

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown during the backoff. The failure is already recorded; leaving quietly
                // keeps host shutdown prompt.
                return;
            }
        }
    }

    /// <summary>
    /// Runs every phase once, in its own scope and under its own budget. Returns the failure that
    /// ended the attempt, or <see langword="null"/> if it completed.
    /// </summary>
    private async Task<AttemptFailure?> RunAttemptAsync(int attempt, CancellationToken stoppingToken)
    {
        // Per attempt, not per lifetime: a retry that inherited a spent budget would be cancelled
        // before it ran a single phase.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        // Tracked locally so every failure path can attribute the failure to the phase that
        // was actually running (the shared WarmupState only exists for readers).
        string? currentPhase = null;

        // Nothing may escape this try: an exception propagating out of ExecuteAsync would
        // trigger BackgroundServiceExceptionBehavior.StopHost and take the whole app down
        // instead of just failing readiness.
        try
        {
            // A scope per attempt. Whatever the previous attempt left faulted in there — a
            // broken connection, a DbContext that captured the failure — must not be handed to
            // the retry, or the retry fails for a reason that has already gone away.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;

            foreach (var phase in _options.Phases)
            {
                currentPhase = phase.Name;
                await RunPhaseAsync(phase, services, attemptCts.Token);
            }

            return null;
        }
        catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            WarmupTimedOut(_logger, attempt, _options.TimeoutSeconds, ex);
            return new AttemptFailure(currentPhase ?? "timeout", ex);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            WarmupCancelled(_logger, ex);
            return new AttemptFailure(currentPhase ?? "cancelled", ex);
        }
        catch (Exception ex)
        {
            // Logged by the caller, which knows whether this attempt was the last one and can
            // pick the severity accordingly.
            return new AttemptFailure(currentPhase ?? "unknown", ex);
        }
    }

    private async Task RunPhaseAsync(IWarmupPhase phase, IServiceProvider services, CancellationToken attemptToken)
    {
        attemptToken.ThrowIfCancellationRequested();
        _warmupState.MarkPhaseStarted(phase.Name);
        WarmupPhaseStarting(_logger, phase.Name);

        // A per-phase budget is layered under the attempt budget, so a phase that overruns cannot
        // consume the time a later phase needs. Like every timeout built on cancellation tokens,
        // it bounds only work that observes the token.
        // Zero stands for "no per-phase budget": the validator rejects anything below 1, so a
        // real budget is always positive, and this keeps the value non-nullable in the catch.
        var phaseTimeoutSeconds = phase.TimeoutSeconds ?? 0;
        using var phaseCts = phaseTimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(attemptToken)
            : null;
        phaseCts?.CancelAfter(TimeSpan.FromSeconds(phaseTimeoutSeconds));
        var phaseToken = phaseCts?.Token ?? attemptToken;

        try
        {
            await phase.RunAsync(services, phaseToken);
        }
        catch (OperationCanceledException ex) when (phaseCts is { IsCancellationRequested: true } && !attemptToken.IsCancellationRequested)
        {
            // The phase alone ran out of time. Optional phases still only log; required ones
            // fail the attempt, but as a phase timeout rather than a whole-attempt timeout.
            if (phase.Optional)
            {
                WarmupOptionalPhaseTimedOut(_logger, phase.Name, phaseTimeoutSeconds, ex);
                return;
            }

            WarmupPhaseTimedOut(_logger, phase.Name, phaseTimeoutSeconds, ex);
            throw new TimeoutException(
                $"Readiness warmup phase '{phase.Name}' timed out after {phaseTimeoutSeconds}s.", ex);
        }
        catch (Exception ex) when (phase.Optional && !(ex is OperationCanceledException && attemptToken.IsCancellationRequested))
        {
            // Optional phases must never fail readiness, and so never cause a retry either.
            WarmupOptionalPhaseFailed(_logger, phase.Name, ex);
            return;
        }

        WarmupPhaseCompleted(_logger, phase.Name);
    }

    private sealed record AttemptFailure(string Phase, Exception Exception);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Readiness warmup is disabled.")]
    private static partial void WarmupDisabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Queuing readiness warmup.")]
    private static partial void WarmupQueued(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Readiness warmup completed successfully on attempt {Attempt}.")]
    private static partial void WarmupCompleted(ILogger logger, int attempt);

    // Warning rather than Error: an attempt that timed out is retried, and the terminal failure
    // has its own Error line. A retrying instance should not page anyone on its own.
    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Readiness warmup attempt {Attempt} timed out after {TimeoutSeconds}s.")]
    private static partial void WarmupTimedOut(ILogger logger, int attempt, int timeoutSeconds, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Readiness warmup was cancelled.")]
    private static partial void WarmupCancelled(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Readiness warmup failed in phase {WarmupPhase} after {Attempts} attempt(s); readiness will stay unhealthy.")]
    private static partial void WarmupFailed(ILogger logger, string warmupPhase, int attempts, Exception exception);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Starting readiness warmup phase {WarmupPhase}.")]
    private static partial void WarmupPhaseStarting(ILogger logger, string warmupPhase);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Completed readiness warmup phase {WarmupPhase}.")]
    private static partial void WarmupPhaseCompleted(ILogger logger, string warmupPhase);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Optional readiness warmup phase {WarmupPhase} failed; readiness will not be failed by this phase.")]
    private static partial void WarmupOptionalPhaseFailed(ILogger logger, string warmupPhase, Exception exception);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Readiness warmup phase {WarmupPhase} timed out after {TimeoutSeconds}s.")]
    private static partial void WarmupPhaseTimedOut(ILogger logger, string warmupPhase, int timeoutSeconds, Exception exception);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Optional readiness warmup phase {WarmupPhase} timed out after {TimeoutSeconds}s; readiness will not be failed by this phase.")]
    private static partial void WarmupOptionalPhaseTimedOut(ILogger logger, string warmupPhase, int timeoutSeconds, Exception exception);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Readiness warmup attempt {Attempt} failed in phase {WarmupPhase}; retrying in {DelaySeconds:F1}s.")]
    private static partial void WarmupAttemptFailed(ILogger logger, int attempt, string warmupPhase, double delaySeconds, Exception exception);
}
