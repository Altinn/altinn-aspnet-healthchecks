using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

/// <summary>
/// <see cref="HealthCheckJsonResponseWriter"/> exists so the library does not need a package
/// dependency for the HealthChecks UI JSON format. These tests pin its output to the reference
/// implementation (<see cref="UIResponseWriter.WriteHealthCheckUIResponse"/>) byte for byte.
/// </summary>
public sealed class HealthCheckJsonResponseWriterTests
{
    public static TheoryData<string, HealthReport> Reports() => new()
    {
        {
            "empty report",
            new HealthReport(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero)
        },
        {
            "healthy with data and tags",
            Report(("postgres", Entry(
                HealthStatus.Healthy,
                description: "up",
                data: new Dictionary<string, object> { ["latencyMs"] = 12.5, ["pool"] = "primary", ["warm"] = true },
                tags: ["dependencies", "critical"])))
        },
        {
            "degraded without description",
            Report(("redis", Entry(HealthStatus.Degraded)))
        },
        {
            "unhealthy with exception and no description",
            Report(("broker", Entry(HealthStatus.Unhealthy, exception: new InvalidOperationException("connection refused"))))
        },
        {
            "unhealthy with exception and description",
            Report(("broker", Entry(
                HealthStatus.Unhealthy,
                description: "broker unreachable",
                exception: new InvalidOperationException("connection refused"))))
        },
        {
            "non-ascii and json-sensitive characters",
            Report(("blåbærsyltetøy", Entry(
                HealthStatus.Healthy,
                description: "ærlig \"sunn\" <og> frisk 🤖",
                data: new Dictionary<string, object> { ["nøkkel"] = "verdi\nmed linjeskift" })))
        },
        {
            "multiple entries with duration",
            new HealthReport(
                new Dictionary<string, HealthReportEntry>
                {
                    ["self"] = Entry(HealthStatus.Healthy, duration: TimeSpan.FromMilliseconds(1.25), tags: ["self"]),
                    ["Endpoints"] = Entry(HealthStatus.Degraded, description: "slow", duration: TimeSpan.FromSeconds(5.5), tags: ["external"]),
                },
                TimeSpan.FromSeconds(5.75))
        },
    };

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Output_is_byte_identical_to_the_reference_UIResponseWriter(string name, HealthReport report)
    {
        Assert.NotNull(name);

        var (ourBody, ourContentType) = await Write(HealthCheckJsonResponseWriter.WriteResponse, report);
        var (referenceBody, referenceContentType) = await Write(UIResponseWriter.WriteHealthCheckUIResponse, report);

        Assert.Equal(referenceContentType, ourContentType);
        Assert.Equal(referenceBody, ourBody);
    }

    [Fact]
    public async Task Suppressing_data_writes_an_empty_data_object()
    {
        // The suppressed body must still be the UI format, so it is pinned against the reference
        // writer's output for the same report with the data removed - not against a literal.
        var withData = Report(("broker", Entry(
            HealthStatus.Healthy,
            description: "Ready",
            data: new Dictionary<string, object> { ["Endpoints"] = "sb://internal.example.no/queue" })));
        var withoutData = Report(("broker", Entry(HealthStatus.Healthy, description: "Ready")));

        var (suppressed, _) = await Write(
            HealthCheckJsonResponseWriter.Create(includeExceptionDetails: true, includeData: false),
            withData);
        var (reference, _) = await Write(UIResponseWriter.WriteHealthCheckUIResponse, withoutData);

        Assert.Equal(reference, suppressed);
        Assert.DoesNotContain("sb://internal.example.no", suppressed, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Data_is_included_by_default(string name, HealthReport report)
    {
        Assert.NotNull(name);

        var (created, _) = await Write(HealthCheckJsonResponseWriter.Create(includeExceptionDetails: true), report);
        var (reference, _) = await Write(UIResponseWriter.WriteHealthCheckUIResponse, report);

        Assert.Equal(reference, created);
    }

    private static async Task<(string Body, string? ContentType)> Write(
        Func<HttpContext, HealthReport, Task> writer,
        HealthReport report)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await writer(context, report);

        return (System.Text.Encoding.UTF8.GetString(body.ToArray()), context.Response.ContentType);
    }

    private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries) =>
        new(entries.ToDictionary(e => e.Name, e => e.Entry), TimeSpan.FromMilliseconds(42));

    private static HealthReportEntry Entry(
        HealthStatus status,
        string? description = null,
        TimeSpan? duration = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? data = null,
        IEnumerable<string>? tags = null) =>
        new(status, description, duration ?? TimeSpan.FromMilliseconds(7), exception, data, tags);
}
