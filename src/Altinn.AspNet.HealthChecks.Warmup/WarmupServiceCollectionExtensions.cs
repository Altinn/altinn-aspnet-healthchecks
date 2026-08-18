using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Opt-in registration for the startup warmup building block. Nothing warmup-related is
/// registered unless <c>AddWarmup</c> is called.
/// </summary>
public static class WarmupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the warmup <see cref="WarmupState"/>, the hosted service that runs the
    /// configured phases, and a readiness health check tagged <see cref="HealthCheckTags.Warmup"/>.
    /// Safe to call more than once: every <paramref name="configure"/> callback is applied
    /// (in call order), while the services and the <c>warmup</c> check are only registered once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the warmup phases and behaviour.</param>
    public static IServiceCollection AddWarmup(this IServiceCollection services, Action<WarmupOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddWarmupOptions().Configure(configure);
        return services.AddWarmupCore();
    }

    /// <summary>
    /// As <see cref="AddWarmup(IServiceCollection, Action{WarmupOptions})"/>, but binds
    /// <see cref="WarmupOptions.Enabled"/> and <see cref="WarmupOptions.TimeoutSeconds"/> from
    /// <paramref name="configuration"/> first. Phases are always code, so they come from
    /// <paramref name="configure"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The configuration section to bind, supplied by the caller — the library never invents a
    /// key name, since an Altinn app's configuration may come from Azure App Configuration rather
    /// than any appsettings file.
    /// </param>
    /// <param name="configure">Optional callback applied after binding; where phases are added.</param>
    /// <example>
    /// <code>
    /// services.AddWarmup(configuration.GetSection("Warmup"), warmup =>
    ///     warmup.AddPhase("npgsql-pool", WarmNpgsqlAsync));
    /// </code>
    /// with <c>{ "Warmup": { "Enabled": true, "TimeoutSeconds": 90 } }</c>.
    /// </example>
    public static IServiceCollection AddWarmup(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<WarmupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddWarmupOptions().Bind(configuration);
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        return services.AddWarmupCore();
    }

    private static OptionsBuilder<WarmupOptions> AddWarmupOptions(this IServiceCollection services) =>
        services.AddOptions<WarmupOptions>();

    private static IServiceCollection AddWarmupCore(this IServiceCollection services)
    {
        // A second check named "warmup" would make MapAltinnHealthChecks throw "Duplicate health
        // checks were registered..." while mapping — a hard startup failure — so register the
        // infrastructure only on the first call (WarmupState doubles as the marker).
        if (services.Any(d => d.ServiceType == typeof(WarmupState)))
        {
            return services;
        }

        services.AddSingleton<WarmupState>();
        services.AddHostedService<WarmupHostedService>();
        services.AddSingleton<IValidateOptions<WarmupOptions>, WarmupOptionsValidator>();

        // Validate at boot rather than when the hosted service first runs, so a bad timeout is a
        // clear startup failure instead of readiness silently stuck Pending.
        services.AddOptions<WarmupOptions>().ValidateOnStart();

        services.AddHealthChecks()
            .AddCheck<WarmupHealthCheck>("warmup", tags: [HealthCheckTags.Warmup]);

        return services;
    }
}
