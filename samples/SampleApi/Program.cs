using Altinn.AspNet.HealthChecks;
using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// 1. Register the convention: the `self` (liveness) check plus the endpoint/tag layout.
//    Outbound probes come from AspNetCore.HealthChecks.Uris (AddUrlGroup); tag them
//    External so they only run on /health/deep. failureStatus: Degraded = soft dependency.
var healthChecks = builder.Services
    .AddAltinnHealthChecks()
    .AddUrlGroup(new Uri("https://example.com"),
        name: "Example",
        failureStatus: HealthStatus.Degraded,
        tags: [HealthCheckTags.External]);

// 2. Register your own dependency checks with the standard tags. When a connection string is
//    configured (ConnectionStrings:Db), this demonstrates the recommended factory-based
//    pattern: the check probes the NpgsqlDataSource registered in DI — the same pool and
//    auth the app uses — instead of taking its own connection string. That also makes it
//    work unchanged with managed identity / passwordless setups, where token acquisition
//    is configured on the data source (see the package README).
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

// 3. Opt in to startup warmup. Readiness stays 503 until the phase completes.
// WARMUP_SECS lets you make the readiness gate observable when probing the sample.
var warmupSeconds = Math.Max(0, builder.Configuration.GetValue("WARMUP_SECS", 2));
builder.Services.AddWarmup(warmup =>
{
    // Keep the overall timeout above the simulated work, or the gate demonstrates a
    // permanent timeout failure instead of flipping to 200.
    warmup.TimeoutSeconds = Math.Max(30, warmupSeconds + 5);
    warmup.AddPhase("prime", async (_, ct) =>
    {
        // Stand-in for real warmup work (open DB pool, compile ORM model, prime caches).
        await Task.Delay(TimeSpan.FromSeconds(warmupSeconds), ct);
    });
});

var app = builder.Build();

app.MapAltinnHealthChecks();
app.MapGet("/", () => "SampleApi. Try /health, /health/deep, /health/liveness, /health/readiness, /health/startup");

app.Run();
