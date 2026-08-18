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
    private static WebApplication BuildApp(
        Action<IServiceCollection>? configureServices = null,
        Action<HealthCheckEndpointOptions>? configureEndpoints = null,
        Action<AltinnHealthCheckOptions>? configureConvention = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddAltinnHealthChecks(configureConvention)
            .AddCheck("database", () => HealthCheckResult.Healthy(), tags: [HealthCheckTags.Dependencies, HealthCheckTags.Critical])
            // Stand-in for an outbound probe (e.g. AddUrlGroup from AspNetCore.HealthChecks.Uris).
            .AddCheck("maskinporten", () => HealthCheckResult.Healthy(), tags: [HealthCheckTags.External]);

        configureServices?.Invoke(builder.Services);

        var endpoints = new HealthCheckEndpointOptions();
        configureEndpoints?.Invoke(endpoints);

        var app = builder.Build();
        app.MapAltinnHealthChecks(endpoints);
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
    public async Task Disabled_endpoint_is_not_mapped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp(configureEndpoints: o => o.Startup.Disable());
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var startup = await client.GetAsync("/health/startup", cancellationToken);
        using var health = await client.GetAsync("/health", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, startup.StatusCode);
        // The others are unaffected.
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Blank_path_leaves_the_endpoint_unmapped_rather_than_serving_from_root()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Configuration binders can produce "" where they cannot produce null, and
        // MapHealthChecks("") really does serve the health payload from /.
        await using var app = BuildApp(configureEndpoints: o => o.Health.Path = "  ");
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var root = await client.GetAsync("/", cancellationToken);
        using var health = await client.GetAsync("/health", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
    }

    [Fact]
    public async Task Custom_path_is_used_and_default_is_gone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp(configureEndpoints: o => o.Deep.Path = "/internal/health/deep");
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var moved = await client.GetAsync("/internal/health/deep", cancellationToken);
        using var original = await client.GetAsync("/health/deep", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, original.StatusCode);
    }

    [Fact]
    public async Task Endpoint_conventions_are_applied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp(configureEndpoints: o =>
            o.Deep.Configure = endpoint => endpoint.RequireHost("trusted.example"));
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var wrongHost = await client.GetAsync("/health/deep", cancellationToken);

        // The TestServer client sends Host: localhost, which the convention rejects.
        Assert.Equal(HttpStatusCode.NotFound, wrongHost.StatusCode);
    }

    [Fact]
    public async Task Self_check_name_is_configurable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp(configureConvention: o => o.SelfCheckName = "process-self");
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/health/liveness", cancellationToken);

        Assert.Equal(["process-self"], entries);
    }

    [Fact]
    public async Task Exception_details_are_suppressed_when_disabled()
    {
        const string Secret = "Host=db.internal;Password=hunter2";
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var app = BuildApp(
            services => services.AddHealthChecks().AddCheck(
                "failing",
                () => throw new InvalidOperationException(Secret),
                tags: [HealthCheckTags.Dependencies]),
            configureEndpoints: o => o.IncludeExceptionDetails = false);
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        // Neither the exception field nor the description fallback may leak it.
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries").GetProperty("failing");
        Assert.False(entry.TryGetProperty("exception", out _));
        Assert.False(entry.TryGetProperty("description", out _));
    }

    [Fact]
    public async Task Entry_data_is_suppressed_when_disabled()
    {
        // A healthy third-party check can publish broker addresses and queue names through its data,
        // so this is suppressed independently of exception details.
        const string BrokerAddress = "sb://internal.example.no/some-queue";
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var app = BuildApp(
            services => services.AddHealthChecks().AddCheck(
                "broker",
                () => HealthCheckResult.Healthy("Ready", new Dictionary<string, object> { ["Endpoints"] = BrokerAddress }),
                tags: [HealthCheckTags.Dependencies]),
            configureEndpoints: o => o.IncludeData = false);
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain(BrokerAddress, json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries").GetProperty("broker");
        // The data object stays, so the body is still the UI format; only its contents are gone.
        Assert.Empty(entry.GetProperty("data").EnumerateObject());
        // Suppressing data must not suppress the description, which the check chose to publish.
        Assert.Equal("Ready", entry.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Entry_data_is_included_by_default()
    {
        const string BrokerAddress = "sb://internal.example.no/some-queue";
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var app = BuildApp(services => services.AddHealthChecks().AddCheck(
            "broker",
            () => HealthCheckResult.Healthy("Ready", new Dictionary<string, object> { ["Endpoints"] = BrokerAddress }),
            tags: [HealthCheckTags.Dependencies]));
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains(BrokerAddress, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exception_details_are_included_by_default()
    {
        const string Secret = "Host=db.internal;Password=hunter2";
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var app = BuildApp(services => services.AddHealthChecks().AddCheck(
            "failing",
            () => throw new InvalidOperationException(Secret),
            tags: [HealthCheckTags.Dependencies]));
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains(Secret, json, StringComparison.Ordinal);
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
