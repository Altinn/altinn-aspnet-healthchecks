using System.Text.Json;
using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

public class HealthEndpointRoutingTests
{
    private static WebApplication BuildApp(Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddAltinnHealthChecks()
            .AddCheck("database", () => HealthCheckResult.Healthy(), tags: [HealthCheckTags.Dependencies, HealthCheckTags.Critical])
            // Stand-in for an outbound probe (e.g. AddUrlGroup from AspNetCore.HealthChecks.Uris).
            .AddCheck("maskinporten", () => HealthCheckResult.Healthy(), tags: [HealthCheckTags.External]);

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapAltinnHealthChecks();
        return app;
    }

    private static async Task<HashSet<string>> GetEntryNamesAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("entries").EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    [Fact]
    public async Task Liveness_contains_only_self()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp();
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/health/liveness", cancellationToken);

        Assert.Equal(["self"], entries);
    }

    [Fact]
    public async Task Default_health_contains_dependencies_not_external()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp();
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/health", cancellationToken);

        Assert.Contains("database", entries);
        Assert.DoesNotContain("maskinporten", entries);
        Assert.DoesNotContain("self", entries);
    }

    [Fact]
    public async Task Deep_health_adds_external_checks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp();
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/health/deep", cancellationToken);

        Assert.Contains("database", entries);
        Assert.Contains("maskinporten", entries);
    }

    [Fact]
    public async Task Readiness_gates_on_warmup_then_recovers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource();
        await using var app = BuildApp(services =>
            services.AddWarmup(o => o.AddPhase("gate", async (_, ct) => await gate.Task.WaitAsync(ct))));
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using (var pending = await client.GetAsync("/health/readiness", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        }

        gate.SetResult();

        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        for (var i = 0; i < 50 && status != HttpStatusCode.OK; i++)
        {
            using var response = await client.GetAsync("/health/readiness", cancellationToken);
            status = response.StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
