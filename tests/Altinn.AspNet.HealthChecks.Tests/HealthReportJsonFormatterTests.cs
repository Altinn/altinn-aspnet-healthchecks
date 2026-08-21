using System.Buffers;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

/// <summary>
/// The wire format is this library's contract with every dashboard and script that reads a health
/// endpoint, so it is pinned to literal bytes rather than parsed and spot-checked. A diff here is
/// the intended way to notice that the format moved.
/// </summary>
public sealed class HealthReportJsonFormatterTests
{
    public static TheoryData<string, HealthReport, string> Reports() => new()
    {
        {
            "empty report",
            HealthReports.Empty(),
            """{"status":"healthy","totalDuration":"00:00:00","entries":{}}"""
        },
        {
            "healthy with data and tags",
            HealthReports.HealthyWithDataAndTags(),
            """{"status":"healthy","totalDuration":"00:00:00.0420000","entries":{"postgres":{"status":"healthy","duration":"00:00:00.0070000","description":"up","data":{"latencyMs":12.5,"pool":"primary","warm":true},"tags":["dependencies","critical"]}}}"""
        },
        {
            // No description: the property is absent, not null.
            "degraded without description",
            HealthReports.DegradedWithoutDescription(),
            """{"status":"degraded","totalDuration":"00:00:00.0420000","entries":{"redis":{"status":"degraded","duration":"00:00:00.0070000"}}}"""
        },
        {
            "unhealthy with exception and no description",
            HealthReports.UnhealthyWithException(),
            """{"status":"unhealthy","totalDuration":"00:00:00.0420000","entries":{"broker":{"status":"unhealthy","duration":"00:00:00.0070000","exception":{"message":"connection refused"}}}}"""
        },
        {
            // Pins the field order: exception before description.
            "unhealthy with exception and description",
            HealthReports.UnhealthyWithExceptionAndDescription(),
            """{"status":"unhealthy","totalDuration":"00:00:00.0420000","entries":{"broker":{"status":"unhealthy","duration":"00:00:00.0070000","exception":{"message":"connection refused"},"description":"broker unreachable"}}}"""
        },
        {
            // The most valuable golden in the set: it pins the encoder. Norwegian letters pass
            // through unescaped so a raw body stays readable, while quotes, angle brackets and
            // astral-plane characters are escaped.
            "non-ascii and json-sensitive characters",
            HealthReports.NonAsciiAndJsonSensitive(),
            // Written with the escape sequences spelled out, because a raw string literal does not
            // process them — this is the byte-for-byte body.
            """{"status":"healthy","totalDuration":"00:00:00.0420000","entries":{"blåbærsyltetøy":{"status":"healthy","duration":"00:00:00.0070000","description":"ærlig \u0022sunn\u0022 \u003Cog\u003E frisk \uD83E\uDD16","data":{"nøkkel":"verdi\nmed linjeskift"}}}}"""
        },
        {
            "multiple entries with duration",
            HealthReports.MultipleEntries(),
            """{"status":"degraded","totalDuration":"00:00:05.7500000","entries":{"self":{"status":"healthy","duration":"00:00:00.0012500","tags":["live"]},"Endpoints":{"status":"degraded","duration":"00:00:05.5000000","description":"slow","tags":["external"]}}}"""
        },
        {
            // Empty data is omitted entirely. The previous format always emitted an object here,
            // to stay parseable as HealthChecks UI JSON; that constraint is gone.
            "data present but empty",
            HealthReports.EmptyData(),
            """{"status":"healthy","totalDuration":"00:00:00.0420000","entries":{"cache":{"status":"healthy","duration":"00:00:00.0070000"}}}"""
        },
        {
            "tags present but empty",
            HealthReports.EmptyTags(),
            """{"status":"healthy","totalDuration":"00:00:00.0420000","entries":{"cache":{"status":"healthy","duration":"00:00:00.0070000"}}}"""
        },
        {
            "nested inner exceptions",
            HealthReports.NestedExceptions(),
            """{"status":"unhealthy","totalDuration":"00:00:00.0420000","entries":{"broker":{"status":"unhealthy","duration":"00:00:00.0070000","exception":{"message":"outer","innerException":{"message":"middle","innerException":{"message":"inner"}}}}}}"""
        },
        {
            // A status outside the enum reads as unhealthy rather than throwing from the writer.
            "status outside the enum",
            HealthReports.UnknownStatus(),
            """{"status":"healthy","totalDuration":"00:00:00.0420000","entries":{"weird":{"status":"unhealthy","duration":"00:00:00.0070000"}}}"""
        },
    };

    [Theory]
    [MemberData(nameof(Reports))]
    public void Writes_the_pinned_format(string name, HealthReport report, string expected)
    {
        Assert.NotNull(name);

        Assert.Equal(expected, Write(report, HealthReportDetailLevel.Full));
    }

    public static TheoryData<HealthReportDetailLevel, string> Levels() => new()
    {
        {
            HealthReportDetailLevel.Minimal,
            """{"status":"unhealthy","totalDuration":"00:00:00.0420000","entries":{"broker":{"status":"unhealthy","duration":"00:00:00.0070000"}}}"""
        },
        {
            // Tags appear; the description does not, because the entry carries an exception.
            HealthReportDetailLevel.Summary,
            """{"status":"unhealthy","totalDuration":"00:00:00.0420000","entries":{"broker":{"status":"unhealthy","duration":"00:00:00.0070000","tags":["dependencies"]}}}"""
        },
        {
            HealthReportDetailLevel.Diagnostic,
            """{"status":"unhealthy","totalDuration":"00:00:00.0420000","entries":{"broker":{"status":"unhealthy","duration":"00:00:00.0070000","exception":{"message":"connection refused"},"description":"broker unreachable","data":{"endpoint":"sb://internal.example.no"},"tags":["dependencies"]}}}"""
        },
        {
            // Identical to Diagnostic here: this exception was constructed, not thrown, so it has
            // no stack trace to add. Stack traces are covered separately below.
            HealthReportDetailLevel.Full,
            """{"status":"unhealthy","totalDuration":"00:00:00.0420000","entries":{"broker":{"status":"unhealthy","duration":"00:00:00.0070000","exception":{"message":"connection refused"},"description":"broker unreachable","data":{"endpoint":"sb://internal.example.no"},"tags":["dependencies"]}}}"""
        },
    };

    [Theory]
    [MemberData(nameof(Levels))]
    public void Detail_level_gates_each_field(HealthReportDetailLevel detailLevel, string expected)
    {
        var report = HealthReports.Report(("broker", HealthReports.Entry(
            HealthStatus.Unhealthy,
            description: "broker unreachable",
            exception: new InvalidOperationException("connection refused"),
            data: new Dictionary<string, object> { ["endpoint"] = "sb://internal.example.no" },
            tags: ["dependencies"])));

        Assert.Equal(expected, Write(report, detailLevel));
    }

    [Fact]
    public void Stack_traces_are_written_only_at_full_detail()
    {
        // Not goldened: a stack trace's contents depend on the runtime and the JIT.
        var report = HealthReports.Report(("broker", HealthReports.Entry(
            HealthStatus.Unhealthy,
            exception: HealthReports.Thrown())));

        Assert.Contains("\"stackTrace\"", Write(report, HealthReportDetailLevel.Full), StringComparison.Ordinal);
        Assert.DoesNotContain("\"stackTrace\"", Write(report, HealthReportDetailLevel.Diagnostic), StringComparison.Ordinal);
    }

    [Fact]
    public void A_description_is_withheld_from_a_failing_entry_below_diagnostic()
    {
        // HealthCheckService puts a thrown exception's message in Description, so suppressing only
        // the exception field would publish the secret anyway.
        const string Secret = "Host=db.internal;Password=hunter2";
        var report = HealthReports.WithSecret(Secret);

        var summary = Write(report, HealthReportDetailLevel.Summary);

        Assert.DoesNotContain(Secret, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\"description\"", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\"", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\"exception\"", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inner_exception_chain_is_capped()
    {
        Exception exception = new InvalidOperationException("depth-0");
        for (var depth = 1; depth < 12; depth++)
        {
            exception = new InvalidOperationException($"depth-{depth}", exception);
        }

        var written = Write(
            HealthReports.Report(("deep", HealthReports.Entry(HealthStatus.Unhealthy, exception: exception))),
            HealthReportDetailLevel.Full);

        // Five exceptions in total, so four nested under the outermost one.
        Assert.Equal(4, Count(written, "\"innerException\""));
    }

    private static int Count(string haystack, string needle)
    {
        var found = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }

    private static string Write(HealthReport report, HealthReportDetailLevel detailLevel)
    {
        var buffer = new ArrayBufferWriter<byte>();
        HealthReportJsonFormatter.Write(buffer, report, detailLevel);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
