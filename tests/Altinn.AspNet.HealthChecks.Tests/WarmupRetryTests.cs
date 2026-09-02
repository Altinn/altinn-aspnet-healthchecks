using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

/// <summary>
/// The retry loop, driven against <see cref="WarmupHostedService"/> directly rather than through a
/// host: the tests need to hold the clock, and a fake one is only useful if nothing else can move it.
/// </summary>
public class WarmupRetryTests
{
    [Fact]
    public async Task Failed_attempt_is_retried_until_it_succeeds()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup => warmup.AddPhase("db-pool", (_, _) =>
        {
            calls++;
            return calls < 3
                ? Task.FromException(new InvalidOperationException("Name or service not known"))
                : Task.CompletedTask;
        }));

        await harness.RunToCompletionAsync();

        // The incident this exists for: a transient failure on the first attempt used to latch for
        // the lifetime of the process, because nothing ever ran the phases again.
        Assert.Equal(3, calls);
        var snapshot = harness.State.GetSnapshot();
        Assert.Equal(WarmupStatus.Healthy, snapshot.Status);
        Assert.Equal(3, snapshot.Attempt);
    }

    [Fact]
    public async Task Successful_warmup_runs_once()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup => warmup.AddPhase("db-pool", (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }));

        await harness.RunToCompletionAsync();

        Assert.Equal(1, calls);
        Assert.Equal(1, harness.State.GetSnapshot().Attempt);
    }

    [Fact]
    public async Task Each_attempt_runs_in_a_fresh_scope()
    {
        var scopes = new List<ScopeMarker>();
        await using var harness = Harness.For(warmup => warmup.AddPhase("db-pool", (services, _) =>
        {
            scopes.Add(services.GetRequiredService<ScopeMarker>());
            return scopes.Count < 2 ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask;
        }));

        await harness.RunToCompletionAsync();

        // Whatever the failed attempt left faulted in its scope — a broken connection, a DbContext
        // that captured the error — must not be what the retry runs against.
        Assert.Equal(2, scopes.Count);
        Assert.NotSame(scopes[0], scopes[1]);
    }

    [Fact]
    public async Task Retrying_stops_at_MaxAttempts()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup =>
        {
            warmup.Retry.MaxAttempts = 3;
            warmup.AddPhase("db-pool", (_, _) =>
            {
                calls++;
                return Task.FromException(new InvalidOperationException("dns"));
            });
        });

        await harness.RunToCompletionAsync();

        Assert.Equal(3, calls);
        var snapshot = harness.State.GetSnapshot();
        Assert.Equal(WarmupStatus.Failed, snapshot.Status);
        Assert.Equal("db-pool", snapshot.FailedPhase);
        Assert.Equal(3, snapshot.Attempt);
    }

    [Fact]
    public async Task MaxAttempts_of_one_does_not_retry()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup =>
        {
            // The escape hatch for anyone who wants the original single-shot behaviour.
            warmup.Retry.MaxAttempts = 1;
            warmup.AddPhase("db-pool", (_, _) =>
            {
                calls++;
                return Task.FromException(new InvalidOperationException("dns"));
            });
        });

        await harness.RunToCompletionAsync();

        Assert.Equal(1, calls);
        Assert.Equal(WarmupStatus.Failed, harness.State.GetSnapshot().Status);
    }

    [Fact]
    public async Task Optional_phase_failure_does_not_trigger_a_retry()
    {
        var optionalCalls = 0;
        var requiredCalls = 0;
        await using var harness = Harness.For(warmup => warmup
            .AddPhase("optional", (_, _) =>
            {
                optionalCalls++;
                return Task.FromException(new InvalidOperationException("boom"));
            }, optional: true)
            .AddPhase("required", (_, _) =>
            {
                requiredCalls++;
                return Task.CompletedTask;
            }));

        await harness.RunToCompletionAsync();

        // An optional phase never fails the attempt, so there is nothing to retry.
        Assert.Equal(1, optionalCalls);
        Assert.Equal(1, requiredCalls);
        Assert.Equal(WarmupStatus.Healthy, harness.State.GetSnapshot().Status);
    }

    [Fact]
    public async Task Retry_in_flight_is_Pending_and_still_reports_the_previous_failure()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        await using var harness = Harness.For(warmup => warmup.AddPhase("db-pool", async (_, cancellationToken) =>
        {
            if (++calls == 1)
            {
                throw new InvalidOperationException("Name or service not known");
            }

            await gate.Task.WaitAsync(cancellationToken);
        }));

        await harness.StartAsync();
        Assert.True(await harness.AdvanceUntilAsync(() => calls >= 2), "the retry never started");

        var snapshot = harness.State.GetSnapshot();
        Assert.Equal(WarmupStatus.Pending, snapshot.Status);
        Assert.Equal(2, snapshot.Attempt);
        Assert.Equal("db-pool", snapshot.CurrentPhase);
        // Retained across the transition: an instance that is retrying should still be able to say
        // why it is not ready.
        Assert.Equal("db-pool", snapshot.FailedPhase);
        Assert.NotNull(snapshot.Exception);

        var result = await new WarmupHealthCheck(harness.State)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("attempt 2", result.Description, StringComparison.Ordinal);
        Assert.Contains("previous attempt failed", result.Description, StringComparison.Ordinal);
        // Deliberately unattached: an entry with an exception loses its description at Summary
        // detail, and the phase that is retrying is the part a first responder needs.
        Assert.Null(result.Exception);

        gate.SetResult();
        await harness.RunToCompletionAsync(start: false);
    }

    [Fact]
    public async Task Shutdown_during_the_backoff_ends_the_loop()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup =>
        {
            // An hour of backoff the test never advances past, so the loop is certainly parked in
            // the delay when the host stops.
            warmup.Retry.InitialDelaySeconds = 3600;
            warmup.Retry.MaxDelaySeconds = 3600;
            warmup.AddPhase("db-pool", (_, _) =>
            {
                calls++;
                return Task.FromException(new InvalidOperationException("dns"));
            });
        });

        await harness.StartAsync();
        Assert.True(await Harness.WaitUntilAsync(() => calls >= 1), "the first attempt never ran");

        await harness.StopAsync();

        Assert.True(harness.Service.ExecuteTask is { IsCompleted: true });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Attempt_that_exceeds_the_run_budget_is_retried()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup =>
        {
            // Real seconds: CancellationTokenSource.CancelAfter runs on the system clock, not on
            // the TimeProvider the backoff uses.
            warmup.TimeoutSeconds = 1;
            warmup.AddPhase("db-pool", async (_, cancellationToken) =>
            {
                if (++calls == 1)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
            });
        });

        await harness.RunToCompletionAsync();

        // The budget now bounds one attempt. Before, it bounded the process: a retry would have
        // inherited a spent token and failed without running anything.
        Assert.Equal(2, calls);
        Assert.Equal(WarmupStatus.Healthy, harness.State.GetSnapshot().Status);
    }

    [Fact]
    public async Task Phase_that_exceeds_its_own_budget_is_retried()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup => warmup.AddPhase("db-pool", async (_, cancellationToken) =>
        {
            if (++calls == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }, timeoutSeconds: 1));

        await harness.RunToCompletionAsync();

        Assert.Equal(2, calls);
        Assert.Equal(WarmupStatus.Healthy, harness.State.GetSnapshot().Status);
    }

    [Fact]
    public async Task Disabled_warmup_completes_without_running_a_phase()
    {
        var calls = 0;
        await using var harness = Harness.For(warmup =>
        {
            warmup.Enabled = false;
            warmup.AddPhase("db-pool", (_, _) =>
            {
                calls++;
                return Task.FromException(new InvalidOperationException("dns"));
            });
        });

        await harness.RunToCompletionAsync();

        Assert.Equal(0, calls);
        Assert.Equal(WarmupStatus.Healthy, harness.State.GetSnapshot().Status);
    }

    private sealed class ScopeMarker;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly FakeTimeProvider _time = new();

        private Harness(WarmupOptions options)
        {
            _provider = new ServiceCollection().AddScoped<ScopeMarker>().BuildServiceProvider();
            State = new WarmupState();
            Service = new WarmupHostedService(
                NullLogger<WarmupHostedService>.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                State,
                _time,
                Options.Create(options));
        }

        public WarmupHostedService Service { get; }

        public WarmupState State { get; }

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        public static Harness For(Action<WarmupOptions> configure)
        {
            var options = new WarmupOptions();
            configure(options);
            return new Harness(options);
        }

        public Task StartAsync() => Service.StartAsync(Token);

        public Task StopAsync() => Service.StopAsync(Token);

        /// <summary>
        /// Runs the loop to the end — success, or retries exhausted — and surfaces anything it threw.
        /// </summary>
        public async Task RunToCompletionAsync(bool start = true)
        {
            if (start)
            {
                await StartAsync();
            }

            Assert.True(
                await AdvanceUntilAsync(() => Service.ExecuteTask is { IsCompleted: true }),
                "the warmup loop did not finish");

            await Service.ExecuteTask!;
            await StopAsync();
        }

        /// <summary>
        /// Waits for <paramref name="condition"/>, pushing the fake clock along as it goes.
        /// </summary>
        /// <remarks>
        /// A fake clock only fires timers that are already registered, and the loop registers its
        /// backoff timer some moments after the phase fails — a moment the test cannot observe.
        /// Advancing repeatedly sidesteps the question: moving a clock nothing is waiting on costs
        /// nothing, and whichever iteration finds the timer registered releases it.
        /// </remarks>
        public Task<bool> AdvanceUntilAsync(Func<bool> condition) =>
            PollAsync(condition, () => _time.Advance(TimeSpan.FromMinutes(2)));

        /// <summary>As <see cref="AdvanceUntilAsync"/>, but leaves the clock alone.</summary>
        public static Task<bool> WaitUntilAsync(Func<bool> condition) => PollAsync(condition, static () => { });

        private static async Task<bool> PollAsync(Func<bool> condition, Action onPoll)
        {
            for (var i = 0; i < 500; i++)
            {
                if (condition())
                {
                    return true;
                }

                onPoll();
                await Task.Delay(10, Token);
            }

            return condition();
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }
}

public class WarmupBackoffTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    // Capped at MaxDelaySeconds from here on.
    [InlineData(6, 60)]
    [InlineData(20, 60)]
    public void Delay_doubles_until_it_reaches_the_cap(int failedAttempts, double expectedSeconds)
    {
        // A jitter sample of 1 is the top of the band, which is the undiluted curve.
        var delay = WarmupBackoff.ComputeDelay(failedAttempts, new WarmupRetryOptions(), jitterSample: 1);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, 3);
    }

    [Fact]
    public void Jitter_spreads_the_delay_over_the_upper_half_of_the_band()
    {
        var retry = new WarmupRetryOptions();

        // Half the delay is fixed and half is random, so retries scatter — the failures this
        // recovers from hit every instance of a deploy at once — without the rate collapsing.
        Assert.Equal(1, WarmupBackoff.ComputeDelay(1, retry, jitterSample: 0).TotalSeconds, 3);
        Assert.Equal(1.5, WarmupBackoff.ComputeDelay(1, retry, jitterSample: 0.5).TotalSeconds, 3);
        Assert.Equal(2, WarmupBackoff.ComputeDelay(1, retry, jitterSample: 1).TotalSeconds, 3);
    }

    [Fact]
    public void A_long_running_retry_loop_stays_at_the_cap()
    {
        // Unlimited retries mean the attempt count can grow without bound; the exponent must not
        // grow with it.
        var delay = WarmupBackoff.ComputeDelay(int.MaxValue, new WarmupRetryOptions(), jitterSample: 1);

        Assert.Equal(60, delay.TotalSeconds, 3);
    }
}
