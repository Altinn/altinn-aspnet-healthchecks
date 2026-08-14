using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

public class WarmupHealthCheckTests
{
    private static Task<HealthCheckResult> RunAsync(WarmupState state) =>
        new WarmupHealthCheck(state).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Pending_state_is_Unhealthy()
    {
        var result = await RunAsync(new WarmupState());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Complete_state_is_Healthy()
    {
        var state = new WarmupState();
        state.MarkWarmupComplete();

        var result = await RunAsync(state);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Failed_state_is_Unhealthy_and_reports_phase()
    {
        var state = new WarmupState();
        state.MarkWarmupFailed("db-pool", new InvalidOperationException("boom"));

        var result = await RunAsync(state);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("db-pool", result.Description, StringComparison.Ordinal);
    }
}

public class WarmupStateTests
{
    [Fact]
    public void Transitions_track_phase_and_status()
    {
        var state = new WarmupState();
        Assert.Equal(WarmupStatus.Pending, state.GetSnapshot().Status);
        Assert.False(state.GetSnapshot().IsWarmupComplete);

        state.MarkPhaseStarted("phase-1");
        Assert.Equal("phase-1", state.GetSnapshot().CurrentPhase);

        state.MarkWarmupComplete();
        var snapshot = state.GetSnapshot();
        Assert.True(snapshot.IsWarmupComplete);
        Assert.Null(snapshot.CurrentPhase);
    }

    [Fact]
    public void Failure_records_phase_and_exception()
    {
        var state = new WarmupState();
        var ex = new InvalidOperationException("x");

        state.MarkWarmupFailed("phase-2", ex);

        var snapshot = state.GetSnapshot();
        Assert.Equal(WarmupStatus.Failed, snapshot.Status);
        Assert.Equal("phase-2", snapshot.FailedPhase);
        Assert.Same(ex, snapshot.Exception);
    }

    [Fact]
    public async Task Snapshots_stay_consistent_while_writes_are_in_flight()
    {
        const int Transitions = 20_000;
        var state = new WarmupState();
        using var done = new CancellationTokenSource();

        // A concurrent reader must never observe a transition half-applied: a Failed status
        // always carries its phase and exception, a Healthy one carries neither.
        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var snapshot = state.GetSnapshot();
                switch (snapshot.Status)
                {
                    case WarmupStatus.Failed:
                        Assert.NotNull(snapshot.FailedPhase);
                        Assert.NotNull(snapshot.Exception);
                        break;
                    case WarmupStatus.Healthy:
                        Assert.Null(snapshot.FailedPhase);
                        Assert.Null(snapshot.Exception);
                        break;
                    default:
                        break;
                }
            }
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < Transitions; i++)
        {
            state.MarkPhaseStarted($"phase-{i}");
            state.MarkWarmupFailed($"phase-{i}", new InvalidOperationException("x"));
            state.MarkWarmupComplete();
        }

        await done.CancelAsync();
        await reader;
    }
}
