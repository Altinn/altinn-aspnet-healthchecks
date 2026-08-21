using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

/// <summary>
/// Content negotiation. Most cases run against stub formatters with invented media types, so the
/// assertions are about which formatter was picked and what <c>Content-Type</c> it answered with,
/// not about anyone's payload.
/// </summary>
public sealed class HealthReportResponseWriterTests
{
    private const string Json = HealthCheckMediaTypes.Json;

    [Fact]
    public void Requires_at_least_one_formatter()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new HealthReportResponseWriter(HealthReportDetailLevel.Summary, []));

        Assert.Equal("formatters", exception.ParamName);
    }

    [Fact]
    public void Rejects_a_formatter_declaring_no_media_types()
    {
        Assert.Throws<ArgumentException>(() =>
            new HealthReportResponseWriter(HealthReportDetailLevel.Summary, [new StubFormatter()]));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    public void Rejects_a_detail_level_outside_the_ladder(int level)
    {
        // Every gate is level >= X, so an undefined value would clear all of them and publish
        // stack traces and check data as if Full had been asked for.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HealthReportResponseWriter(
                (HealthReportDetailLevel)level,
                [HealthReportJsonFormatter.Instance]));

        Assert.Equal("detailLevel", exception.ParamName);
    }

    [Theory]
    // No preference at all, which is what a kubelet probe sends.
    [InlineData(null, Json)]
    [InlineData("*/*", Json)]
    [InlineData("application/*", Json)]
    [InlineData("application/*+json", Json)]
    // The vendor type is a subset of application/json, so a client asking for generic JSON lands
    // on it — and is told which version it got.
    [InlineData("application/json", Json)]
    [InlineData(Json, Json)]
    [InlineData("text/plain", "text/plain; charset=utf-8")]
    // Quality ordering beats header order.
    [InlineData("text/plain;q=0.9, application/json", Json)]
    [InlineData("application/json;q=0.2, text/plain;q=0.8", "text/plain; charset=utf-8")]
    // Equal quality falls back to the client's listed order, so negotiation is deterministic.
    [InlineData("text/plain, application/json", "text/plain; charset=utf-8")]
    [InlineData("application/json, text/plain", Json)]
    // A browser: text/html first, then */* — which the JSON formatter answers.
    [InlineData("text/html,application/xhtml+xml,*/*;q=0.8", Json)]
    // A type carved out of a range that would otherwise cover it. Acceptability comes from the
    // most specific matching range, so the exclusion beats the wildcard that found it — including
    // when the wildcard is the higher-quality and earlier-listed of the two.
    [InlineData("application/*;q=0.9, " + Json + ";q=0, text/plain;q=0.8", "text/plain; charset=utf-8")]
    [InlineData("*/*, " + Json + ";q=0", "text/plain; charset=utf-8")]
    [InlineData("*/*;q=0.5, text/plain;q=0", Json)]
    // application/*+json is more specific than application/*, so it is the one that decides.
    [InlineData("application/*+json;q=0, application/*;q=0.9, text/plain;q=0.1", "text/plain; charset=utf-8")]
    public async Task Negotiates_the_content_type(string? accept, string expectedContentType)
    {
        var context = await Write(Default(), accept);

        Assert.Equal(expectedContentType, context.Response.ContentType);
    }

    [Theory]
    // Nothing on offer is acceptable...
    [InlineData("application/xml")]
    // ...or everything is explicitly refused. Either way we answer anyway: the middleware has
    // already set 200 or 503, and a 406 would throw away the only thing the caller came for.
    [InlineData("*/*;q=0")]
    [InlineData("application/json;q=0, text/plain;q=0")]
    // Both formats carved out of a wildcard that would otherwise have covered them.
    [InlineData("*/*, " + Json + ";q=0, text/plain;q=0")]
    // Unparseable headers must not fault the endpoint a load balancer is polling.
    [InlineData("%%%")]
    [InlineData("")]
    public async Task Falls_back_to_the_first_formatter(string accept)
    {
        var context = await Write(Default(), accept);

        Assert.Equal(Json, context.Response.ContentType);
        Assert.StartsWith("{", Body(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prepended_formatter_wins_the_no_preference_case()
    {
        var custom = new StubFormatter("application/x-custom");
        var writer = new HealthReportResponseWriter(
            HealthReportDetailLevel.Summary,
            [custom, HealthReportJsonFormatter.Instance]);

        var context = await Write(writer, accept: null);

        Assert.Equal("application/x-custom", context.Response.ContentType);
        Assert.Equal("application/x-custom", custom.WrittenFor);
    }

    [Fact]
    public async Task An_appended_formatter_still_serves_its_own_media_type()
    {
        var custom = new StubFormatter("application/x-custom");
        var writer = new HealthReportResponseWriter(
            HealthReportDetailLevel.Summary,
            [HealthReportJsonFormatter.Instance, custom]);

        // It does not steal the wildcard...
        var wildcard = await Write(writer, "*/*");
        Assert.Equal(Json, wildcard.Response.ContentType);
        Assert.Null(custom.WrittenFor);

        // ...but it does answer a request that names it.
        var named = await Write(writer, "application/x-custom");
        Assert.Equal("application/x-custom", named.Response.ContentType);
        Assert.Equal("application/x-custom", custom.WrittenFor);
    }

    [Fact]
    public async Task Writes_the_body_through_to_the_response_stream()
    {
        // The one test that exercises the real Utf8JsonWriter -> PipeWriter -> flush chain.
        var context = await Write(Default(), accept: null, HealthReports.HealthyWithDataAndTags());

        Assert.Equal(
            """{"status":"healthy","totalDuration":"00:00:00.0420000","entries":{"postgres":{"status":"healthy","duration":"00:00:00.0070000","description":"up","tags":["dependencies","critical"]}}}""",
            Body(context));
    }

    [Fact]
    public async Task Passes_the_detail_level_to_the_formatter()
    {
        var custom = new StubFormatter("application/x-custom");
        var writer = new HealthReportResponseWriter(HealthReportDetailLevel.Minimal, [custom]);

        await Write(writer, accept: null);

        Assert.Equal(HealthReportDetailLevel.Minimal, custom.DetailLevel);
    }

    private static HealthReportResponseWriter Default() =>
        new(
            HealthReportDetailLevel.Summary,
            [HealthReportJsonFormatter.Instance, HealthReportTextFormatter.Instance]);

    private static async Task<HttpContext> Write(
        HealthReportResponseWriter writer,
        string? accept,
        HealthReport? report = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        await writer.WriteAsync(context, report ?? HealthReports.DegradedWithoutDescription());

        return context;
    }

    private static string Body(HttpContext context)
    {
        var stream = (MemoryStream)context.Response.Body;
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Records what it was asked to write instead of writing anything. Constructed with no media
    /// types it is deliberately invalid, for the constructor guard.
    /// </summary>
    private sealed class StubFormatter : HealthReportFormatter
    {
        public StubFormatter(params string[] mediaTypes)
        {
            MediaTypes = [.. mediaTypes.Select(mediaType => MediaTypeHeaderValue.Parse(mediaType).CopyAsReadOnly())];
        }

        public override IReadOnlyList<MediaTypeHeaderValue> MediaTypes { get; }

        public string? WrittenFor { get; private set; }

        public HealthReportDetailLevel? DetailLevel { get; private set; }

        public override Task WriteAsync(HealthReportWriteContext context)
        {
            WrittenFor = context.MediaType.ToString();
            DetailLevel = context.DetailLevel;
            return Task.CompletedTask;
        }
    }
}
