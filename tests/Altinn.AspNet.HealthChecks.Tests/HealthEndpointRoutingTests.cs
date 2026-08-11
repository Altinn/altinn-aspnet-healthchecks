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

    private static async Task<HashSet<string>> GetEntryNamesAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("entries").EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    [Fact]
    public async Task Liveness_contains_only_self()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/health/liveness");

        Assert.Equal(["self"], entries);
    }

    [Fact]
    public async Task Default_health_contains_dependencies_not_external()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/health");

        Assert.Contains("database", entries);
        Assert.DoesNotContain("maskinporten", entries);
        Assert.DoesNotContain("self", entries);
    }

    [Fact]
    public async Task Deep_health_adds_external_checks()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/health/deep");

        Assert.Contains("database", entries);
        Assert.Contains("maskinporten", entries);
    }

    [Fact]
    public async Task Readiness_gates_on_warmup_then_recovers()
    {
        var gate = new TaskCompletionSource();
        await using var app = BuildApp(services =>
            services.AddWarmup(o => o.AddPhase("gate", async (_, ct) => await gate.Task.WaitAsync(ct))));
        await app.StartAsync();
        var client = app.GetTestClient();

        using (var pending = await client.GetAsync("/health/readiness"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        }

        gate.SetResult();

        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        for (var i = 0; i < 50 && status != HttpStatusCode.OK; i++)
        {
            using var response = await client.GetAsync("/health/readiness");
            status = response.StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(50);
            }
        }

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
