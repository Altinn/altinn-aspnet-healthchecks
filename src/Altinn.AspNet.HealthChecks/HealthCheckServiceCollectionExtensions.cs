using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Registration entry points for the Altinn health check convention.
/// </summary>
public static class HealthCheckServiceCollectionExtensions
{
    /// <summary>
    /// Registers health checks with the baseline <c>self</c> liveness check. Register your
    /// dependency checks on the returned builder with the tags from
    /// <see cref="HealthCheckTags"/>, then call
    /// <see cref="HealthCheckEndpointRouteBuilderExtensions.MapAltinnHealthChecks"/>.
    /// Safe to call more than once (like <c>AddHealthChecks</c> itself); the <c>self</c>
    /// check is only registered on the first call.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IHealthChecksBuilder AddAltinnHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddHealthChecks();

        // Registering a second check named "self" would make DefaultHealthCheckService throw
        // "Duplicate health checks were registered..." on every probe (liveness included), so
        // guard with a marker to keep repeated calls (app + shared bootstrap code) harmless.
        if (!services.Any(d => d.ServiceType == typeof(AltinnHealthChecksMarker)))
        {
            services.AddSingleton<AltinnHealthChecksMarker>();
            builder.AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthCheckTags.Self]);
        }

        return builder;
    }

    private sealed class AltinnHealthChecksMarker;
}
