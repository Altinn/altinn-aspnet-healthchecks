using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// An OpenTelemetry span processor that drops ASP.NET Core server spans for health check
/// routes, so probe traffic does not flood traces. Register it with
/// <c>AddHealthCheckActivityFilter</c>.
/// </summary>
/// <remarks>
/// <para>
/// The span is dropped by clearing its <see cref="ActivityTraceFlags.Recorded"/> flag. Both
/// <see cref="SimpleActivityExportProcessor"/> and <see cref="BatchActivityExportProcessor"/>
/// skip activities that are not recorded, so the span reaches no exporter — but only for
/// exporters registered <em>after</em> this processor. Add it before any
/// <c>Add…Exporter()</c> call on the same <see cref="TracerProviderBuilder"/>.
/// </para>
/// <para>
/// Because the decision is made when the server span ends, child spans produced while the
/// probe ran (for example database calls made by a deep check) are unaffected. If you need
/// those dropped too, filter at the instrumentation level instead — see
/// <c>AspNetCoreTraceInstrumentationOptions.Filter</c>.
/// </para>
/// </remarks>
public sealed class HealthCheckActivityFilter : BaseProcessor<Activity>
{
    // The ASP.NET Core ActivitySource name; server request spans originate here.
    private const string AspNetCoreSourcePrefix = "Microsoft.AspNetCore";

    // Exactly one of these is set. The options form stays deferred rather than snapshotting
    // paths, because the tracer provider is normally built before MapAltinnHealthChecks runs.
    private readonly string[]? _explicitSuffixes;
    private readonly Lazy<string[]>? _endpointSuffixes;

    /// <summary>
    /// Creates the filter. When no suffixes are supplied, the defaults cover the five endpoints
    /// mapped by <c>MapAltinnHealthChecks()</c> from the <c>Altinn.AspNet.HealthChecks</c> package:
    /// <c>/alive</c>, <c>/health</c>, <c>/health/readiness</c>, <c>/health/startup</c>
    /// and <c>/health/deep</c>.
    /// </summary>
    /// <remarks>
    /// Matching is by case-insensitive route <em>suffix</em>, so the endpoints stay suppressed
    /// when the app is mounted under a path base. The flip side: any route ending in a suffix is
    /// suppressed too — an app with a business route like <c>/api/devices/{id}/health</c> loses
    /// those spans under the defaults. In that case pass explicit, more specific suffixes.
    /// </remarks>
    public HealthCheckActivityFilter(params string[] suppressedRouteSuffixes)
    {
        _explicitSuffixes = suppressedRouteSuffixes is { Length: > 0 }
            ? suppressedRouteSuffixes
            : ["/alive", "/health", "/health/readiness", "/health/startup", "/health/deep"];
    }

    /// <summary>
    /// Creates the filter from the same <see cref="HealthCheckEndpointOptions"/> instance passed
    /// to <c>MapAltinnHealthChecks</c>, suppressing exactly the endpoints that are mapped.
    /// </summary>
    /// <remarks>
    /// Prefer this over the default suffixes whenever paths are customised. With hardcoded
    /// defaults, moving <c>Deep.Path</c> silently stops suppressing that route — no error, just
    /// probe spans quietly flooding your traces.
    /// <para>
    /// Paths are read on the first span rather than here, because the tracer provider is normally
    /// built during service registration, before <c>MapAltinnHealthChecks</c> runs — snapshotting
    /// at construction would reintroduce the same drift for anything configured in between.
    /// Mutating paths after the app is serving traffic is not supported.
    /// </para>
    /// </remarks>
    public HealthCheckActivityFilter(HealthCheckEndpointOptions endpointOptions)
    {
        ArgumentNullException.ThrowIfNull(endpointOptions);

        _endpointSuffixes = new Lazy<string[]>(() =>
            [.. endpointOptions.All
                .Select(endpoint => endpoint.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)]);
    }

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (!activity.Source.Name.StartsWith(AspNetCoreSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsSuppressedRoute(activity))
        {
            return;
        }

        // Setting IsAllDataRequested here would be pointless — the span is already fully
        // populated, and the flag is only consulted while it is running. Clearing Recorded is
        // what makes the downstream export processors skip it.
        activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }

    private bool IsSuppressedRoute(Activity activity)
    {
        // http.route is set once endpoint routing has matched; url.path covers spans that
        // ended before (or without) a match.
        var route = activity.GetTagItem("http.route") as string
                    ?? activity.GetTagItem("url.path") as string;

        if (route is null)
        {
            return false;
        }

        foreach (var suffix in _explicitSuffixes ?? _endpointSuffixes!.Value)
        {
            if (route.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Extensions for wiring up <see cref="HealthCheckActivityFilter"/>.</summary>
public static class HealthCheckTracerProviderBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="HealthCheckActivityFilter"/> that suppresses trace spans for the
    /// given route suffixes (defaults to the five endpoints mapped by
    /// <c>MapAltinnHealthChecks()</c> from the <c>Altinn.AspNet.HealthChecks</c> package). Call this
    /// before registering exporters — only exporters added after it see the suppression.
    /// </summary>
    public static TracerProviderBuilder AddHealthCheckActivityFilter(
        this TracerProviderBuilder builder,
        params string[] suppressedRouteSuffixes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProcessor(new HealthCheckActivityFilter(suppressedRouteSuffixes));
    }

    /// <summary>
    /// Adds a <see cref="HealthCheckActivityFilter"/> that suppresses trace spans for exactly the
    /// endpoints <paramref name="endpointOptions"/> maps. Pass the same instance to
    /// <c>MapAltinnHealthChecks</c> so customised paths cannot drift out of sync. Call this before
    /// registering exporters — only exporters added after it see the suppression.
    /// </summary>
    public static TracerProviderBuilder AddHealthCheckActivityFilter(
        this TracerProviderBuilder builder,
        HealthCheckEndpointOptions endpointOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProcessor(new HealthCheckActivityFilter(endpointOptions));
    }
}
