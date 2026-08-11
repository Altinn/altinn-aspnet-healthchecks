using Microsoft.Extensions.DependencyInjection;

namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Opt-in registration for the startup warmup building block. Nothing warmup-related is
/// registered unless <see cref="AddWarmup"/> is called.
/// </summary>
public static class WarmupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the warmup <see cref="WarmupState"/>, the hosted service that runs the
    /// configured phases, and a readiness health check tagged <see cref="HealthCheckTags.Warmup"/>.
    /// Call after <see cref="HealthCheckServiceCollectionExtensions.AddAltinnHealthChecks"/>.
    /// Safe to call more than once: every <paramref name="configure"/> callback is applied
    /// (in call order), while the services and the <c>warmup</c> check are only registered once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the warmup phases and behaviour.</param>
    public static IServiceCollection AddWarmup(this IServiceCollection services, Action<WarmupOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<WarmupOptions>().Configure(configure);

        // A second check named "warmup" would make DefaultHealthCheckService throw
        // "Duplicate health checks were registered..." on every probe, so register the
        // infrastructure only on the first call (WarmupState doubles as the marker).
        if (services.Any(d => d.ServiceType == typeof(WarmupState)))
        {
            return services;
        }

        services.AddSingleton<WarmupState>();
        services.AddHostedService<WarmupHostedService>();

        services.AddHealthChecks()
            .AddCheck<WarmupHealthCheck>("warmup", tags: [HealthCheckTags.Warmup]);

        return services;
    }
}
