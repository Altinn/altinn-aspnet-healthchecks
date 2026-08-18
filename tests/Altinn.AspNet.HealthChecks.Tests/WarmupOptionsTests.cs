using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

public class WarmupOptionsTests
{
    private static IHost BuildHost(Action<IServiceCollection> configureServices)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddAltinnHealthChecks();
        configureServices(builder.Services);
        return builder.Build();
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Enabled_and_timeout_bind_from_configuration()
    {
        using var host = BuildHost(services => services.AddWarmup(
            Config(("Warmup:Enabled", "false"), ("Warmup:TimeoutSeconds", "90")).GetSection("Warmup")));

        var options = host.Services.GetRequiredService<IOptions<WarmupOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal(90, options.TimeoutSeconds);
    }

    [Fact]
    public void Configure_callback_runs_after_binding()
    {
        using var host = BuildHost(services => services.AddWarmup(
            Config(("Warmup:TimeoutSeconds", "90")).GetSection("Warmup"),
            warmup => warmup.AddPhase("noop", (_, _) => Task.CompletedTask)));

        var options = host.Services.GetRequiredService<IOptions<WarmupOptions>>().Value;

        Assert.Equal(90, options.TimeoutSeconds);
        Assert.Equal("noop", Assert.Single(options.Phases).Name);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    // Well past any legitimate warmup, and the shape a transposed digit takes.
    [InlineData("86400")]
    public async Task Invalid_run_timeout_fails_host_startup(string timeoutSeconds)
    {
        using var host = BuildHost(services => services.AddWarmup(
            Config(("Warmup:TimeoutSeconds", timeoutSeconds)).GetSection("Warmup")));

        // ValidateOnStart means a bad timeout is a boot failure that names the property, rather
        // than readiness silently stuck Pending.
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(WarmupOptions.TimeoutSeconds), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_warmup_does_not_validate_its_own_configuration()
    {
        // Enabled=false is a kill switch. A bad timeout it never reads must not be able to hold
        // the host down on the very setting an operator reaches for to get past it.
        using var host = BuildHost(services => services.AddWarmup(
            Config(("Warmup:Enabled", "false"), ("Warmup:TimeoutSeconds", "0")).GetSection("Warmup")));

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Invalid_phase_timeout_fails_host_startup_naming_the_phase()
    {
        using var host = BuildHost(services => services.AddWarmup(warmup =>
            warmup.AddPhase("db-pool", (_, _) => Task.CompletedTask, timeoutSeconds: 0)));

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("db-pool", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Optional_phase_timing_out_does_not_fail_readiness()
    {
        var laterPhaseRan = false;
        using var host = BuildHost(services => services.AddWarmup(warmup => warmup
            .AddPhase("slow-optional", (_, ct) => Task.Delay(Timeout.Infinite, ct),
                optional: true, timeoutSeconds: 1)
            .AddPhase("required", (_, _) =>
            {
                laterPhaseRan = true;
                return Task.CompletedTask;
            })));

        await host.StartAsync(TestContext.Current.CancellationToken);

        var state = host.Services.GetRequiredService<WarmupState>();
        for (var i = 0; i < 200 && !state.GetSnapshot().IsWarmupComplete; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        // The per-phase budget is what stops the hung optional phase consuming the whole run and
        // starving the required phase behind it.
        Assert.True(laterPhaseRan);
        Assert.Equal(WarmupStatus.Healthy, state.GetSnapshot().Status);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Required_phase_timing_out_fails_readiness_naming_the_phase()
    {
        using var host = BuildHost(services => services.AddWarmup(warmup =>
            warmup.AddPhase("db-pool", (_, ct) => Task.Delay(Timeout.Infinite, ct),
                timeoutSeconds: 1)));

        await host.StartAsync(TestContext.Current.CancellationToken);

        var state = host.Services.GetRequiredService<WarmupState>();
        for (var i = 0; i < 200 && state.GetSnapshot().Status == WarmupStatus.Pending; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var snapshot = state.GetSnapshot();
        Assert.Equal(WarmupStatus.Failed, snapshot.Status);
        Assert.Equal("db-pool", snapshot.FailedPhase);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
