using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Registration entry points for the Altinn health check convention.
/// </summary>
public static class HealthCheckServiceCollectionExtensions
{
    /// <summary>
    /// Registers health checks with the baseline liveness check. Register your dependency checks
    /// on the returned builder with the tags from <see cref="HealthCheckTags"/>, then call
    /// <see cref="HealthCheckEndpointRouteBuilderExtensions.MapAltinnHealthChecks(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, HealthCheckEndpointOptions)"/>.
    /// Safe to call more than once (like <c>AddHealthChecks</c> itself); the liveness check is only
    /// registered on the first call, and later <paramref name="configure"/> callbacks cannot rename it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the convention.</param>
    public static IHealthChecksBuilder AddAltinnHealthChecks(
        this IServiceCollection services,
        Action<AltinnHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AltinnHealthCheckOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SelfCheckName, nameof(options.SelfCheckName));

        var builder = services.AddHealthChecks();

        // Registering the liveness check twice would make MapAltinnHealthChecks throw
        // "Duplicate health checks were registered..." while mapping — HealthCheckService is
        // resolved there, so it is a hard startup failure, not a per-request one. Guard with a
        // marker to keep repeated calls (app + shared bootstrap code) harmless.
        if (!services.Any(d => d.ServiceType == typeof(AltinnHealthChecksMarker)))
        {
            services.AddSingleton<AltinnHealthChecksMarker>();
            builder.AddCheck(options.SelfCheckName, () => HealthCheckResult.Healthy(), tags: [HealthCheckTags.Self]);
        }

        return builder;
    }

    private sealed class AltinnHealthChecksMarker;
}

/// <summary>Configures the Altinn health check convention's own registrations.</summary>
public sealed class AltinnHealthCheckOptions
{
    /// <summary>
    /// Name of the built-in liveness check, which surfaces on the liveness endpoint. Change it
    /// when the app already registers a check called <c>self</c> — health check names must be
    /// unique, and a collision fails startup.
    /// </summary>
    public string SelfCheckName { get; set; } = "self";
}
