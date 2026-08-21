using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Writes a <see cref="HealthReport"/> to the response body in one or more media types.
/// Registered on <see cref="HealthCheckEndpointOptions.Formatters"/> and selected per request by
/// <see cref="HealthReportResponseWriter"/> through <c>Accept</c> header negotiation.
/// </summary>
/// <remarks>
/// An abstract class rather than an interface, for two reasons: members can be added in a later
/// version without breaking implementers, and the detail-level helpers below — in particular
/// <see cref="MayWriteDescription"/>, which encodes a leak that is easy to get wrong — are shared
/// rather than reimplemented by everyone who writes a formatter.
/// </remarks>
public abstract class HealthReportFormatter
{
    /// <summary>Initializes the formatter.</summary>
    protected HealthReportFormatter()
    {
    }

    /// <summary>
    /// The media types this formatter produces, most preferred first. Must be concrete: a wildcard
    /// here would match an <c>Accept</c> range without telling the client what it is getting.
    /// </summary>
    /// <remarks>
    /// A list rather than a single value so a formatter can answer several unrelated types — a
    /// versioned type alongside the one it supersedes, say. Whichever entry matched becomes the
    /// response's <c>Content-Type</c>, so declaring parameters here (a <c>charset</c>, as
    /// <see cref="HealthReportTextFormatter"/> does) puts them on the wire. Prefer read-only
    /// instances; <see cref="MediaTypeHeaderValue"/> is otherwise mutable.
    /// </remarks>
    public abstract IReadOnlyList<MediaTypeHeaderValue> MediaTypes { get; }

    /// <summary>
    /// Which of this formatter's <see cref="MediaTypes"/> satisfies <paramref name="accept"/>, or
    /// <see langword="null"/> if none does.
    /// </summary>
    /// <param name="accept">
    /// One range from the request's <c>Accept</c> header. May be a wildcard (<c>*/*</c>,
    /// <c>application/*</c>, <c>application/*+json</c>).
    /// </param>
    /// <remarks>
    /// The default walks <see cref="MediaTypes"/> in order and returns the first that
    /// <see cref="MediaTypeHeaderValue.IsSubsetOf"/> the requested range, which is the precedence
    /// RFC 9110 describes — wildcards included. Override only for matching a listed type cannot
    /// express.
    /// </remarks>
    public virtual MediaTypeHeaderValue? SelectMediaType(MediaTypeHeaderValue accept)
    {
        ArgumentNullException.ThrowIfNull(accept);

        var mediaTypes = MediaTypes;
        for (var i = 0; i < mediaTypes.Count; i++)
        {
            if (mediaTypes[i].IsSubsetOf(accept))
            {
                return mediaTypes[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Writes <see cref="HealthReportWriteContext.Report"/> to the response body. The status code
    /// and <c>Content-Type</c> are already set; do not change them.
    /// </summary>
    public abstract Task WriteAsync(HealthReportWriteContext context);

    /// <summary>
    /// Whether an entry's description may be written at <paramref name="detailLevel"/>.
    /// </summary>
    /// <remarks>
    /// Below <see cref="HealthReportDetailLevel.Diagnostic"/> this is false for any entry carrying
    /// an exception, however innocuous its description looks. When a check throws,
    /// <see cref="HealthCheckService"/> builds the entry with the exception message <em>as</em> the
    /// description, so a formatter that suppresses <c>exception</c> but writes <c>description</c>
    /// publishes the connection string regardless.
    /// </remarks>
    protected static bool MayWriteDescription(HealthReportEntry entry, HealthReportDetailLevel detailLevel) =>
        entry.Description is not null
        && (detailLevel >= HealthReportDetailLevel.Diagnostic
            || (detailLevel >= HealthReportDetailLevel.Summary && entry.Exception is null));

    /// <summary>Whether an entry's tags may be written at <paramref name="detailLevel"/>.</summary>
    protected static bool MayWriteTags(HealthReportDetailLevel detailLevel) =>
        detailLevel >= HealthReportDetailLevel.Summary;

    /// <summary>
    /// Whether an entry's <c>data</c> may be written at <paramref name="detailLevel"/>. False for
    /// an empty dictionary — an empty object says nothing worth the bytes.
    /// </summary>
    protected static bool MayWriteData(HealthReportEntry entry, HealthReportDetailLevel detailLevel) =>
        detailLevel >= HealthReportDetailLevel.Diagnostic && entry.Data is { Count: > 0 };

    /// <summary>Whether an entry's exception may be written at <paramref name="detailLevel"/>.</summary>
    protected static bool MayWriteException(HealthReportEntry entry, HealthReportDetailLevel detailLevel) =>
        detailLevel >= HealthReportDetailLevel.Diagnostic && entry.Exception is not null;

    /// <summary>
    /// Whether stack traces and inner exceptions may be written at <paramref name="detailLevel"/>.
    /// </summary>
    protected static bool MayWriteExceptionDetails(HealthReportDetailLevel detailLevel) =>
        detailLevel >= HealthReportDetailLevel.Full;
}
