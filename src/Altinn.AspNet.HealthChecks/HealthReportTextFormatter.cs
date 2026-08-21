using System.Buffers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Writes a <see cref="HealthReport"/> as <see cref="HealthCheckMediaTypes.PlainText"/>: the
/// overall status and nothing else, as one lowercase word.
/// </summary>
/// <remarks>
/// Deliberately minimal. This is what a human gets from a bare <c>curl -H 'Accept: text/plain'</c>
/// and what the status code already said; anything worth reading comes from
/// <see cref="HealthReportJsonFormatter"/>. Individual entries are never written here, so the
/// response cannot leak a check's description or data regardless of
/// <see cref="HealthReportWriteContext.DetailLevel"/>.
/// </remarks>
public sealed class HealthReportTextFormatter : HealthReportFormatter
{
    /// <summary>The shared instance. The formatter is stateless.</summary>
    public static HealthReportTextFormatter Instance { get; } = new();

    private static ReadOnlySpan<byte> Healthy => "healthy"u8;

    private static ReadOnlySpan<byte> Degraded => "degraded"u8;

    private static ReadOnlySpan<byte> Unhealthy => "unhealthy"u8;

    // The charset belongs in the media type rather than bolted onto the header later: a type with
    // extra parameters is still a subset of the bare `text/plain` a client asks for, and
    // ToString() then yields the exact Content-Type we want to send.
    private readonly IReadOnlyList<MediaTypeHeaderValue> _mediaTypes =
    [
        MediaTypeHeaderValue.Parse(HealthCheckMediaTypes.PlainText + "; charset=utf-8").CopyAsReadOnly(),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<MediaTypeHeaderValue> MediaTypes => _mediaTypes;

    /// <inheritdoc />
    public override async Task WriteAsync(HealthReportWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var body = context.HttpContext.Response.BodyWriter;
        body.Write(ToText(context.Report.Status));
        await body.FlushAsync(context.CancellationToken).ConfigureAwait(false);
    }

    // As in the JSON formatter: an out-of-range status degrades to the safest reading rather than
    // throwing from a response writer.
    private static ReadOnlySpan<byte> ToText(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => Healthy,
        HealthStatus.Degraded => Degraded,
        _ => Unhealthy,
    };
}
