using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Maps the Altinn health check endpoints onto an <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class HealthCheckEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the health endpoints (liveness, readiness, startup, health, deep), each filtering the
    /// registered checks by tag. Endpoints whose <see cref="HealthEndpoint.Path"/> is
    /// <see langword="null"/> are skipped.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (e.g. the <c>WebApplication</c>).</param>
    /// <param name="configure">Optional callback to customise paths, route conventions and response detail.</param>
    public static IEndpointRouteBuilder MapAltinnHealthChecks(
        this IEndpointRouteBuilder endpoints,
        Action<HealthCheckEndpointOptions>? configure = null)
    {
        var options = new HealthCheckEndpointOptions();
        configure?.Invoke(options);
        return endpoints.MapAltinnHealthChecks(options);
    }

    /// <summary>
    /// Maps the health endpoints from an options instance you own. Prefer this overload when the
    /// same instance is also passed to <c>AddHealthCheckActivityFilter</c> from the
    /// <c>Altinn.AspNet.HealthChecks.OpenTelemetry</c> package, so customised paths cannot drift
    /// out of sync with trace suppression.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (e.g. the <c>WebApplication</c>).</param>
    /// <param name="options">The endpoint configuration.</param>
    public static IEndpointRouteBuilder MapAltinnHealthChecks(
        this IEndpointRouteBuilder endpoints,
        HealthCheckEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        // One writer for all endpoints, built once from the configured detail level.
        var writer = HealthCheckJsonResponseWriter.Create(options.IncludeExceptionDetails);

        return endpoints
            .MapHealthCheckEndpoint(options.Startup, writer, static check => check.Tags.Contains(HealthCheckTags.Dependencies))
            .MapHealthCheckEndpoint(options.Liveness, writer, static check => check.Tags.Contains(HealthCheckTags.Self))
            .MapHealthCheckEndpoint(options.Readiness, writer, static check => check.Tags.Contains(HealthCheckTags.Critical) || check.Tags.Contains(HealthCheckTags.Warmup))
            .MapHealthCheckEndpoint(options.Health, writer, static check => check.Tags.Contains(HealthCheckTags.Dependencies))
            .MapHealthCheckEndpoint(options.Deep, writer, static check => check.Tags.Contains(HealthCheckTags.Dependencies) || check.Tags.Contains(HealthCheckTags.External));
    }

    private static IEndpointRouteBuilder MapHealthCheckEndpoint(
        this IEndpointRouteBuilder endpoints,
        HealthEndpoint endpoint,
        Func<Microsoft.AspNetCore.Http.HttpContext, HealthReport, Task> writer,
        Func<HealthCheckRegistration, bool> predicate)
    {
        // Blank counts as unmapped, not as "map me at the site root". Configuration binders can
        // produce "" where they cannot produce null, and MapHealthChecks("") really does serve
        // the health payload from /. Treating both the same way also keeps this in step with the
        // OpenTelemetry filter, which derives its suppressed routes from these same paths.
        if (string.IsNullOrWhiteSpace(endpoint.Path))
        {
            return endpoints;
        }

        var path = endpoint.Path;

        var builder = endpoints.MapHealthChecks(path, new HealthCheckOptions
        {
            Predicate = predicate,
            ResponseWriter = writer
        });

        endpoint.Configure?.Invoke(builder);
        return endpoints;
    }
}
