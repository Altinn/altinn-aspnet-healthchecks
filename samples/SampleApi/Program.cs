using Altinn.AspNet.HealthChecks;
using Altinn.AspNet.HealthChecks.Probes;
using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// 1. Decide the endpoint layout up front, so the same instance can drive both the route mapping
//    and (in a real app) OpenTelemetry trace suppression — see AddHealthCheckActivityFilter in
//    the Altinn.AspNet.HealthChecks.OpenTelemetry package. Sharing one instance is what stops a
//    customised path from silently desyncing the two.
//    DetailLevel is left unset on purpose: it then follows the environment, so exception messages
//    and check data stay out of a production body without the app having to say so. Run with
//    ASPNETCORE_ENVIRONMENT=Development / Staging / Production to see the three levels.
var healthEndpoints = new HealthCheckEndpointOptions();

// 2. Register the convention: the liveness check plus the endpoint/tag layout.
var healthChecks = builder.Services.AddAltinnHealthChecks();

// 3. Outbound probes of upstream services. Tagged External by the package, so they only run on
//    /health/deep. Hard = Unhealthy (we are broken without it); soft = Degraded.
//    Options are per call, not per chain, so the same callback goes to both — otherwise the
//    code-registered probe below would quietly fall back to the 10s default.
void ConfigureProbes(OutboundProbeOptions probes)
{
    // Lets probe entries use RelativePath instead of a full URL, so the same configuration
    // works in every environment with only the base URI changing.
    probes.BaseUri = new Uri("https://example.com/");
    probes.Timeout = TimeSpan.FromSeconds(5);
}

healthChecks
    .AddOutboundProbes(builder.Configuration.GetSection("HealthProbes"), ConfigureProbes)
    .AddOutboundProbe("Example", new Uri("https://example.com"), hard: false, configure: ConfigureProbes);

// 4. Register your own dependency checks with the standard tags. When a connection string is
//    configured (ConnectionStrings:Db), this demonstrates the recommended factory-based
//    pattern: the check probes the NpgsqlDataSource registered in DI — the same pool and
//    auth the app uses — instead of taking its own connection string.
var dbConnectionString = builder.Configuration.GetConnectionString("Db");
if (dbConnectionString is not null)
{
    builder.Services.AddNpgsqlDataSource(dbConnectionString);
    healthChecks.AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(),
        name: "database",
        tags: [HealthCheckTags.Dependencies, HealthCheckTags.Critical]);
}
else
{
    // Fake stand-in so the sample runs without a database. Critical (fails readiness)
    // and a dependency (shows on /health and /health/deep).
    healthChecks.AddCheck("database", () => HealthCheckResult.Healthy("Command executed successfully"),
        tags: [HealthCheckTags.Dependencies, HealthCheckTags.Critical]);
}

// 5. Opt in to startup warmup. Readiness stays 503 until the phase completes.
//    Enabled/Timeout bind from the "Warmup" section; phases are always code.
//    WARMUP_SECS lets you make the readiness gate observable when probing the sample.
var warmupSeconds = Math.Max(0, builder.Configuration.GetValue("WARMUP_SECS", 2));
builder.Services.AddWarmup(builder.Configuration.GetSection("Warmup"), warmup =>
{
    // Keep the overall budget above the simulated work, or the gate demonstrates a
    // permanent timeout failure instead of flipping to 200.
    warmup.TimeoutSeconds = Math.Max(30, warmupSeconds + 5);
    warmup.AddPhase("prime", async (_, ct) =>
    {
        // Stand-in for real warmup work (open DB pool, compile ORM model, prime caches).
        await Task.Delay(TimeSpan.FromSeconds(warmupSeconds), ct);
    },
    // A per-phase budget stops one slow phase eating the whole run's time.
    timeoutSeconds: Math.Max(20, warmupSeconds + 5));
});

var app = builder.Build();

app.MapAltinnHealthChecks(healthEndpoints);
app.MapGet("/", () => "SampleApi. Try /alive, /health, /health/deep, /health/readiness, /health/startup");

app.Run();
