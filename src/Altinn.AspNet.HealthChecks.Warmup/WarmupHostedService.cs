using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Runs the configured <see cref="IWarmupPhase"/> set on startup (off the startup path, so it
/// does not block the host from listening) and records progress in <see cref="WarmupState"/>.
/// </summary>
internal sealed partial class WarmupHostedService : BackgroundService
{
    // CancellationTokenSource.CancelAfter is backed by a timer whose maximum delay is
    // uint.MaxValue - 2 milliseconds (~49.7 days); larger values make CancelAfter throw.
    internal const int MaxTimeoutSeconds = (int)((uint.MaxValue - 2) / 1000);

    private readonly ILogger<WarmupHostedService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WarmupState _warmupState;
    private readonly WarmupOptions _options;

    public WarmupHostedService(
        ILogger<WarmupHostedService> logger,
        IServiceScopeFactory scopeFactory,
        WarmupState warmupState,
        IOptions<WarmupOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(warmupState);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _scopeFactory = scopeFactory;
        _warmupState = warmupState;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Everything before the first await runs synchronously inside StartAsync, so a throw
        // here surfaces as a host startup failure and the Enabled early-out completes before
        // the host starts listening.
        if (!_options.Enabled)
        {
            WarmupDisabled(_logger);
            _warmupState.MarkWarmupComplete();
            return;
        }

        if (_options.TimeoutSeconds is <= 0 or > MaxTimeoutSeconds)
        {
            // Fail host startup with a clear configuration error. Left unvalidated, a
            // non-positive timeout would either fault the background task silently
            // (readiness stuck Pending) or report a spurious instant timeout, and an
            // over-large one would make CancelAfter throw inside the background task,
            // permanently failing readiness with a misleading error.
            throw new InvalidOperationException(
                $"{nameof(WarmupOptions)}.{nameof(WarmupOptions.TimeoutSeconds)} must be between 1 and {MaxTimeoutSeconds}, but was {_options.TimeoutSeconds}.");
        }

        WarmupQueued(_logger);

        // Hop off the startup path so a phase that blocks before its first await cannot
        // stall host startup.
        await Task.Yield();

        await PerformWarmupAsync(stoppingToken);
    }

    private async Task PerformWarmupAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        // Tracked locally so every failure path can attribute the failure to the phase that
        // was actually running (the shared WarmupState only exists for readers).
        string? currentPhase = null;

        // Nothing may escape this try: an exception propagating out of ExecuteAsync would
        // trigger BackgroundServiceExceptionBehavior.StopHost and take the whole app down
        // instead of just failing readiness.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;

            foreach (var phase in _options.Phases)
            {
                currentPhase = phase.Name;
                await RunPhaseAsync(phase, services, timeoutCts.Token);
            }

            WarmupCompleted(_logger);
            _warmupState.MarkWarmupComplete();
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            WarmupTimedOut(_logger, _options.TimeoutSeconds, ex);
            _warmupState.MarkWarmupFailed(currentPhase ?? "timeout", ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            WarmupCancelled(_logger, ex);
            _warmupState.MarkWarmupFailed(currentPhase ?? "cancelled", ex);
        }
        catch (Exception ex)
        {
            WarmupFailed(_logger, ex);
            _warmupState.MarkWarmupFailed(currentPhase ?? "unknown", ex);
        }
    }

    private async Task RunPhaseAsync(IWarmupPhase phase, IServiceProvider services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _warmupState.MarkPhaseStarted(phase.Name);
        WarmupPhaseStarting(_logger, phase.Name);

        try
        {
            await phase.RunAsync(services, cancellationToken);
        }
        catch (Exception ex) when (phase.Optional && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Optional phases must never fail readiness.
            WarmupOptionalPhaseFailed(_logger, phase.Name, ex);
            return;
        }

        WarmupPhaseCompleted(_logger, phase.Name);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Readiness warmup is disabled.")]
    private static partial void WarmupDisabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Queuing readiness warmup.")]
    private static partial void WarmupQueued(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Readiness warmup completed successfully.")]
    private static partial void WarmupCompleted(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Readiness warmup timed out after {TimeoutSeconds}s.")]
    private static partial void WarmupTimedOut(ILogger logger, int timeoutSeconds, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Readiness warmup was cancelled.")]
    private static partial void WarmupCancelled(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Readiness warmup failed.")]
    private static partial void WarmupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Starting readiness warmup phase {WarmupPhase}.")]
    private static partial void WarmupPhaseStarting(ILogger logger, string warmupPhase);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Completed readiness warmup phase {WarmupPhase}.")]
    private static partial void WarmupPhaseCompleted(ILogger logger, string warmupPhase);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Optional readiness warmup phase {WarmupPhase} failed; readiness will not be failed by this phase.")]
    private static partial void WarmupOptionalPhaseFailed(ILogger logger, string warmupPhase, Exception exception);
}
