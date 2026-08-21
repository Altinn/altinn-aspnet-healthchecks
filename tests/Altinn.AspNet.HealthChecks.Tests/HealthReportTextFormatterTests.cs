using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

public sealed class HealthReportTextFormatterTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, "healthy")]
    [InlineData(HealthStatus.Degraded, "degraded")]
    [InlineData(HealthStatus.Unhealthy, "unhealthy")]
    public async Task Writes_the_overall_status(HealthStatus status, string expected)
    {
        var report = HealthReports.Report(("check", HealthReports.Entry(status)));

        Assert.Equal(expected, await Write(report, HealthReportDetailLevel.Full));
    }

    [Fact]
    public async Task Writes_nothing_a_check_authored_even_at_full_detail()
    {
        // The plain-text body is the overall status and nothing else, so it cannot leak a
        // description, an exception message or a check's data whatever the detail level says.
        const string Secret = "Host=db.internal;Password=hunter2";

        var written = await Write(HealthReports.WithSecret(Secret), HealthReportDetailLevel.Full);

        Assert.Equal("unhealthy", written);
    }

    [Fact]
    public async Task Sets_the_charset_on_the_content_type()
    {
        var writer = new HealthReportResponseWriter(
            HealthReportDetailLevel.Summary,
            [HealthReportTextFormatter.Instance]);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await writer.WriteAsync(context, HealthReports.Empty());

        Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
    }

    private static async Task<string> Write(HealthReport report, HealthReportDetailLevel detailLevel)
    {
        var writer = new HealthReportResponseWriter(detailLevel, [HealthReportTextFormatter.Instance]);

        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await writer.WriteAsync(context, report);

        return Encoding.UTF8.GetString(body.ToArray());
    }
}
