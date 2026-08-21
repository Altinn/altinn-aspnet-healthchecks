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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="detailLevel"/> is not one of the declared levels.
    /// </exception>
    public HealthReportResponseWriter(
        HealthReportDetailLevel detailLevel,
        IEnumerable<HealthReportFormatter> formatters)
    {
        // The levels are gated with level >= X, so an out-of-range value clears every gate and is
        // silently treated as Full — stack traces and check data in production, from a cast or a
        // configuration binder. That direction of failure is the wrong one for this option, so it
        // is rejected here, where the mistake is still a startup error.
        if (!Enum.IsDefined(detailLevel))
        {
            throw new ArgumentOutOfRangeException(
                nameof(detailLevel),
                detailLevel,
                $"Not a declared {nameof(HealthReportDetailLevel)}.");
        }

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
            HealthReportFormatter? bestFormatter = null;
            MediaTypeHeaderValue? bestMediaType = null;
            var bestQuality = 0d;
            var bestRange = int.MaxValue;

            // Ranges outermost, so SelectMediaType stays the single place matching is decided and a
            // formatter is only ever asked about a range the client actually sent.
            for (var r = 0; r < ranges.Count; r++)
            {
                for (var f = 0; f < _formatters.Length; f++)
                {
                    if (_formatters[f].SelectMediaType(ranges[r]) is not { } mediaType)
                    {
                        continue;
                    }

                    var (quality, range) = EffectiveQuality(mediaType, ranges, r);

                    // "q=0" means explicitly unacceptable, not "least preferred".
                    if (quality <= 0)
                    {
                        continue;
                    }

                    // Ties go to the range the client listed first, and only then — when two
                    // formats matched the same range — to our own preference order. Both halves
                    // keep negotiation deterministic for a given header.
                    if (quality > bestQuality || (quality == bestQuality && range < bestRange))
                    {
                        bestFormatter = _formatters[f];
                        bestMediaType = mediaType;
                        bestQuality = quality;
                        bestRange = range;
                    }
                }
            }

            if (bestFormatter is not null)
            {
                return (bestFormatter, bestMediaType!);
            }
        }

        // Nothing acceptable, or no preference expressed. Fall back rather than answering 406: the
        // health check middleware has already set 200 or 503, and replacing a 503 with a 406 throws
        // away the only thing the caller came for.
        return (_formatters[0], _formatters[0].MediaTypes[0]);
    }

    // The quality the client attached to mediaType, which RFC 9110 takes from the *most specific*
    // range covering it — not from whichever range happened to find it. A client may accept a
    // wildcard and carve one type out of it (application/*;q=0.9, application/x-thing;q=0), and the
    // narrower exclusion has to beat the wildcard that matched. Returns that range's position in
    // the header too, so header order can break quality ties.
    private static (double Quality, int RangeIndex) EffectiveQuality(
        MediaTypeHeaderValue mediaType,
        IList<MediaTypeHeaderValue> ranges,
        int matchedAt)
    {
        // Seeded from the range that produced the match, which is the answer for a SelectMediaType
        // override matching something IsSubsetOf below cannot see.
        var quality = ranges[matchedAt].Quality ?? 1d;
        var index = matchedAt;
        var bestSpecificity = -1;

        for (var i = 0; i < ranges.Count; i++)
        {
            if (!mediaType.IsSubsetOf(ranges[i]))
            {
                continue;
            }

            var specificity = Specificity(ranges[i]);

            // Strictly greater: equally specific ranges resolve to the first one listed.
            if (specificity > bestSpecificity)
            {
                bestSpecificity = specificity;
                quality = ranges[i].Quality ?? 1d;
                index = i;
            }
        }

        return (quality, index);
    }

    // Media-range precedence: a concrete type beats type/*+suffix, beats type/*, beats */*, with
    // parameters breaking the remaining ties. Clamped so a pathological parameter count cannot
    // promote a wildcard past a concrete type.
    private static int Specificity(MediaTypeHeaderValue range)
    {
        var tier = range switch
        {
            { MatchesAllTypes: true } => 0,
            { MatchesAllSubTypes: true } => 1,
            { MatchesAllSubTypesWithoutSuffix: true } => 2,
            _ => 3,
        };

        var parameters = range.Parameters.Count - (range.Quality.HasValue ? 1 : 0);

        return (tier * 100) + Math.Clamp(parameters, 0, 99);
    }
}
