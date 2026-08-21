using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Picks a <see cref="HealthReportFormatter"/> from the request's <c>Accept</c> header and writes
/// the report with it. <see cref="WriteAsync"/> is assignable to
/// <see cref="Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions.ResponseWriter"/>.
/// </summary>
/// <remarks>
/// <c>MapAltinnHealthChecks</c> builds one of these for all its endpoints. Construct your own to
/// reuse the convention's body format on an endpoint you map yourself:
/// <code>
/// var writer = new HealthReportResponseWriter(
///     HealthReportDetailLevel.Summary,
///     [HealthReportJsonFormatter.Instance]);
///
/// app.MapHealthChecks("/custom", new HealthCheckOptions
/// {
///     ResponseWriter = writer.WriteAsync
/// });
/// </code>
/// </remarks>
public sealed class HealthReportResponseWriter
{
    private readonly HealthReportFormatter[] _formatters;

    /// <summary>Creates the writer.</summary>
    /// <param name="detailLevel">How much of each report may be written.</param>
    /// <param name="formatters">
    /// Candidate formatters in preference order. The first is used when the client expresses no
    /// usable preference, so put the format most callers want there.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="formatters"/> is empty, or contains a formatter declaring no media types —
    /// both are startup mistakes, and failing here beats failing on every probe.
    /// </exception>
    public HealthReportResponseWriter(
        HealthReportDetailLevel detailLevel,
        IEnumerable<HealthReportFormatter> formatters)
    {
        ArgumentNullException.ThrowIfNull(formatters);

        _formatters = [.. formatters];

        if (_formatters.Length == 0)
        {
            throw new ArgumentException("At least one formatter is required.", nameof(formatters));
        }

        foreach (var formatter in _formatters)
        {
            if (formatter is null)
            {
                throw new ArgumentException("Formatters cannot be null.", nameof(formatters));
            }

            if (formatter.MediaTypes is not { Count: > 0 })
            {
                throw new ArgumentException(
                    $"Formatter '{formatter.GetType()}' declares no media types.",
                    nameof(formatters));
            }
        }

        DetailLevel = detailLevel;
    }

    /// <summary>How much of each report is written.</summary>
    public HealthReportDetailLevel DetailLevel { get; }

    /// <summary>The candidate formatters, in preference order.</summary>
    public IReadOnlyList<HealthReportFormatter> Formatters => _formatters;

    /// <summary>Negotiates a format and writes <paramref name="report"/> to the response body.</summary>
    public Task WriteAsync(HttpContext httpContext, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(report);

        var (formatter, mediaType) = Negotiate(httpContext.Request);

        // Must precede the formatter: writing the body starts the response, after which headers are
        // no longer mutable.
        httpContext.Response.ContentType = mediaType.ToString();

        return formatter.WriteAsync(new HealthReportWriteContext(httpContext, report, DetailLevel, mediaType));
    }

    private (HealthReportFormatter Formatter, MediaTypeHeaderValue MediaType) Negotiate(HttpRequest request)
    {
        var accept = (string[]?)request.Headers.Accept;

        // TryParseList is the lenient parser: it skips entries it cannot read rather than throwing
        // or rejecting the whole header. That is what we want on an endpoint a load balancer hits
        // every few seconds with whatever it happens to send.
        if (accept is { Length: > 0 } && MediaTypeHeaderValue.TryParseList(accept, out var ranges) && ranges.Count > 0)
        {
            // QualityComparer implements the Accept precedence rules: higher q first, and among
            // equal q values, specific types before subtype wildcards before */*. OrderByDescending
            // is a stable sort, so genuinely equivalent ranges keep the client's listed order and
            // negotiation stays deterministic.
            foreach (var range in ranges.OrderByDescending(range => range, MediaTypeHeaderValueComparer.QualityComparer))
            {
                // "q=0" means explicitly unacceptable, not "least preferred".
                if (range.Quality == 0)
                {
                    continue;
                }

                foreach (var formatter in _formatters)
                {
                    if (formatter.SelectMediaType(range) is { } mediaType)
                    {
                        return (formatter, mediaType);
                    }
                }
            }
        }

        // Nothing acceptable, or no preference expressed. Fall back rather than answering 406: the
        // health check middleware has already set 200 or 503, and replacing a 503 with a 406 throws
        // away the only thing the caller came for.
        return (_formatters[0], _formatters[0].MediaTypes[0]);
    }
}
