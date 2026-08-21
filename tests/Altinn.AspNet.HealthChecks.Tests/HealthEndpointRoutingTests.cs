using System.Text.Json;
using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
#if NET9_0_OR_GREATER
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
#endif
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
        Action<AltinnHealthCheckOptions>? configureConvention = null,
        // Environments.Production is a static field, not a constant, so it cannot be the default.
        string environmentName = "Production")
    {
        // The environment must be pinned. Left to CreateBuilder it comes from ASPNETCORE_ENVIRONMENT
        // in whatever shell is running the suite, and the detail level defaults to whatever that
        // implies — green on one machine and red on another, for reasons invisible from the test.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName
        });
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

        var entries = await GetEntryNamesAsync(client, "/alive", cancellationToken);

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
    public async Task Liveness_can_be_moved_back_to_the_pre_alive_path()
    {
        // The default moved to /alive, matching the Microsoft/Aspire scaffolding. Deployments
        // already probing something else override the path rather than changing their manifests.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp(configureEndpoints: o => o.Liveness.Path = "/health/liveness");
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var moved = await client.GetAsync("/health/liveness", cancellationToken);
        using var original = await client.GetAsync("/alive", cancellationToken);

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

        var entries = await GetEntryNamesAsync(client, "/alive", cancellationToken);

        Assert.Equal(["process-self"], entries);
    }

    [Fact]
    public async Task Checks_tagged_live_surface_on_liveness()
    {
        // The tag value matches the Microsoft/Aspire scaffolding, so a check registered as
        // AddCheck("…", …, ["live"]) needs no retagging to end up here.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp(services => services.AddHealthChecks().AddCheck(
            "gc",
            () => HealthCheckResult.Healthy(),
            tags: ["live"]));
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        var entries = await GetEntryNamesAsync(client, "/alive", cancellationToken);

        Assert.Contains("gc", entries);
    }

    [Fact]
    public async Task Production_publishes_neither_exception_details_nor_data()
    {
        const string Secret = "Host=db.internal;Password=hunter2";
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var app = BuildApp(services =>
        {
            services.AddHealthChecks()
                .AddCheck("failing", () => throw new InvalidOperationException(Secret), tags: [HealthCheckTags.Dependencies])
                // A healthy third-party check can publish broker addresses through its data.
                .AddCheck(
                    "broker",
                    () => HealthCheckResult.Healthy("Ready", new Dictionary<string, object> { ["Endpoints"] = Secret }),
                    tags: [HealthCheckTags.Dependencies]);
        });
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        // Neither the exception field, the description fallback, nor the data may leak it.
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries");

        var failing = entries.GetProperty("failing");
        Assert.False(failing.TryGetProperty("exception", out _));
        Assert.False(failing.TryGetProperty("description", out _));

        var broker = entries.GetProperty("broker");
        Assert.False(broker.TryGetProperty("data", out _));
        // A description is safe on an entry that did not fail, so it survives.
        Assert.Equal("Ready", broker.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Development_publishes_stack_traces()
    {
        const string Secret = "Host=db.internal;Password=hunter2";
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var app = BuildApp(
            services => services.AddHealthChecks().AddCheck(
                "failing",
                () => throw new InvalidOperationException(Secret),
                tags: [HealthCheckTags.Dependencies]),
            environmentName: Environments.Development);
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var exception = doc.RootElement.GetProperty("entries").GetProperty("failing").GetProperty("exception");

        Assert.Equal(Secret, exception.GetProperty("message").GetString());
        Assert.True(exception.TryGetProperty("stackTrace", out _));
    }

    [Fact]
    public async Task An_explicit_detail_level_overrides_the_environment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var app = BuildApp(
            configureEndpoints: o => o.DetailLevel = HealthReportDetailLevel.Minimal,
            environmentName: Environments.Development);
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries").GetProperty("database");

        // Minimal writes nothing a check authored — not even its tags.
        Assert.False(entry.TryGetProperty("tags", out _));
        Assert.False(entry.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Plain_text_is_served_when_asked_for()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp();
        await app.StartAsync(cancellationToken);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Accept", "text/plain");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

#if NET9_0_OR_GREATER
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http_metrics_suppression_follows_the_option(bool disableHttpMetrics)
    {
        // The failure mode is silent — a target-framework guard or a wiring regression puts probe
        // traffic back into http.server.request.duration with nothing to see in a response.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = BuildApp(configureEndpoints: o => o.DisableHttpMetrics = disableHttpMetrics);
        await app.StartAsync(cancellationToken);

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is "/alive" or "/health"
                or "/health/readiness" or "/health/startup" or "/health/deep")
            .ToList();

        Assert.Equal(5, endpoints.Count);
        Assert.All(endpoints, endpoint =>
        {
            var metadata = endpoint.Metadata.GetMetadata<IDisableHttpMetricsMetadata>();

            if (disableHttpMetrics)
            {
                Assert.NotNull(metadata);
            }
            else
            {
                Assert.Null(metadata);
            }
        });
    }
#endif

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
