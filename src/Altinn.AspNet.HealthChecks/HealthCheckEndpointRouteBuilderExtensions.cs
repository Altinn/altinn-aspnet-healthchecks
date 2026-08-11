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
    /// Maps the five health endpoints (liveness, readiness, startup, health, deep), each
    /// filtering registered checks by tag. All endpoints emit the standard HealthChecks UI
    /// JSON via <see cref="HealthCheckJsonResponseWriter"/>, so the deep endpoint is
    /// structurally identical to the Dialogporten reference deployment.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (e.g. the <c>WebApplication</c>).</param>
    /// <param name="configure">Optional callback to override the default route paths.</param>
    public static IEndpointRouteBuilder MapAltinnHealthChecks(
        this IEndpointRouteBuilder endpoints,
        Action<HealthCheckEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = new HealthCheckEndpointOptions();
        configure?.Invoke(options);

        return endpoints
            .MapHealthCheckEndpoint(options.StartupPath, check => check.Tags.Contains(HealthCheckTags.Dependencies))
            .MapHealthCheckEndpoint(options.LivenessPath, check => check.Tags.Contains(HealthCheckTags.Self))
            .MapHealthCheckEndpoint(options.ReadinessPath, check => check.Tags.Contains(HealthCheckTags.Critical) || check.Tags.Contains(HealthCheckTags.Warmup))
            .MapHealthCheckEndpoint(options.HealthPath, check => check.Tags.Contains(HealthCheckTags.Dependencies))
            .MapHealthCheckEndpoint(options.DeepPath, check => check.Tags.Contains(HealthCheckTags.Dependencies) || check.Tags.Contains(HealthCheckTags.External));
    }

    private static IEndpointRouteBuilder MapHealthCheckEndpoint(
        this IEndpointRouteBuilder endpoints,
        string path,
        Func<HealthCheckRegistration, bool> predicate)
    {
        endpoints.MapHealthChecks(path, new HealthCheckOptions
        {
            Predicate = predicate,
            ResponseWriter = HealthCheckJsonResponseWriter.WriteResponse
        });
        return endpoints;
    }
}
