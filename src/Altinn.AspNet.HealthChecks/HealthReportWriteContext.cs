using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Everything a <see cref="HealthReportFormatter"/> needs to write one response.
/// </summary>
/// <remarks>
/// Constructed by <see cref="HealthReportResponseWriter"/> once per request. The constructor is
/// public so a custom formatter can be unit tested without a request pipeline.
/// </remarks>
public sealed class HealthReportWriteContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="httpContext">The request being answered.</param>
    /// <param name="report">The report to write.</param>
    /// <param name="detailLevel">How much of <paramref name="report"/> may be written.</param>
    /// <param name="mediaType">
    /// The media type negotiation selected — one of the formatter's own
    /// <see cref="HealthReportFormatter.MediaTypes"/>. <see cref="HealthReportResponseWriter"/> has
    /// already written it to the <c>Content-Type</c> header.
    /// </param>
    public HealthReportWriteContext(
        HttpContext httpContext,
        HealthReport report,
        HealthReportDetailLevel detailLevel,
        MediaTypeHeaderValue mediaType)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(mediaType);

        HttpContext = httpContext;
        Report = report;
        DetailLevel = detailLevel;
        MediaType = mediaType;
    }

    /// <summary>
    /// The request being answered. Use <see cref="HttpContext.RequestServices"/> for anything a
    /// formatter needs from the container — that is why this type carries no service provider of
    /// its own, and why formatters can be plain objects rather than DI registrations.
    /// </summary>
    public HttpContext HttpContext { get; }

    /// <summary>The report to write.</summary>
    public HealthReport Report { get; }

    /// <summary>How much of <see cref="Report"/> may be written.</summary>
    public HealthReportDetailLevel DetailLevel { get; }

    /// <summary>The media type negotiation selected, already set as the response content type.</summary>
    public MediaTypeHeaderValue MediaType { get; }

    /// <summary>Shorthand for <see cref="HttpContext.RequestAborted"/>.</summary>
    public CancellationToken CancellationToken => HttpContext.RequestAborted;
}
