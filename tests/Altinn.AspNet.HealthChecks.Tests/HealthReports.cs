using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Altinn.AspNet.HealthChecks.Tests;

/// <summary>
/// The report fixtures the formatter tests are pinned against. Shared so the JSON and plain-text
/// formatters are exercised on exactly the same inputs.
/// </summary>
internal static class HealthReports
{
    /// <summary>Total duration shared by every fixture, so goldens differ only where they mean to.</summary>
    public static readonly TimeSpan TotalDuration = TimeSpan.FromMilliseconds(42);

    /// <summary>An exception that was actually thrown, so it carries a real stack trace.</summary>
    public static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("connection refused");
        }
        catch (InvalidOperationException caught)
        {
            return caught;
        }
    }

    public static HealthReport Empty() =>
        new(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero);

    public static HealthReport HealthyWithDataAndTags() =>
        Report(("postgres", Entry(
            HealthStatus.Healthy,
            description: "up",
            data: new Dictionary<string, object> { ["latencyMs"] = 12.5, ["pool"] = "primary", ["warm"] = true },
            tags: ["dependencies", "critical"])));

    public static HealthReport DegradedWithoutDescription() =>
        Report(("redis", Entry(HealthStatus.Degraded)));

    public static HealthReport UnhealthyWithException() =>
        Report(("broker", Entry(
            HealthStatus.Unhealthy,
            exception: new InvalidOperationException("connection refused"))));

    public static HealthReport UnhealthyWithExceptionAndDescription() =>
        Report(("broker", Entry(
            HealthStatus.Unhealthy,
            description: "broker unreachable",
            exception: new InvalidOperationException("connection refused"))));

    public static HealthReport NonAsciiAndJsonSensitive() =>
        Report(("blåbærsyltetøy", Entry(
            HealthStatus.Healthy,
            description: "ærlig \"sunn\" <og> frisk 🤖",
            data: new Dictionary<string, object> { ["nøkkel"] = "verdi\nmed linjeskift" })));

    public static HealthReport MultipleEntries() =>
        new(
            new Dictionary<string, HealthReportEntry>
            {
                ["self"] = Entry(HealthStatus.Healthy, duration: TimeSpan.FromMilliseconds(1.25), tags: ["live"]),
                ["Endpoints"] = Entry(HealthStatus.Degraded, description: "slow", duration: TimeSpan.FromSeconds(5.5), tags: ["external"]),
            },
            TimeSpan.FromSeconds(5.75));

    /// <summary>Data present but empty — the entry must carry no <c>data</c> property at all.</summary>
    public static HealthReport EmptyData() =>
        Report(("cache", Entry(
            HealthStatus.Healthy,
            data: new Dictionary<string, object>())));

    /// <summary>Tags present but empty — the entry must carry no <c>tags</c> property at all.</summary>
    public static HealthReport EmptyTags() =>
        Report(("cache", Entry(HealthStatus.Healthy, tags: [])));

    public static HealthReport NestedExceptions() =>
        Report(("broker", Entry(
            HealthStatus.Unhealthy,
            exception: new InvalidOperationException(
                "outer",
                new TimeoutException("middle", new IOException("inner"))))));

    /// <summary>A status outside the enum, which a response writer must survive.</summary>
    public static HealthReport UnknownStatus() =>
        Report(("weird", Entry((HealthStatus)42)));

    /// <summary>A check whose failure carries a secret, for the leak assertions.</summary>
    public static HealthReport WithSecret(string secret) =>
        Report(("database", Entry(
            HealthStatus.Unhealthy,
            description: secret,
            exception: new InvalidOperationException(secret),
            data: new Dictionary<string, object> { ["connectionString"] = secret })));

    public static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries) =>
        new(entries.ToDictionary(e => e.Name, e => e.Entry), TotalDuration);

    public static HealthReportEntry Entry(
        HealthStatus status,
        string? description = null,
        TimeSpan? duration = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? data = null,
        IEnumerable<string>? tags = null) =>
        new(status, description, duration ?? TimeSpan.FromMilliseconds(7), exception, data, tags);
}
