using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Registers a health check only if nothing has claimed its name yet.
/// </summary>
/// <remarks>
/// Health check names must be unique, and a collision is not a per-request problem: mapping the
/// endpoints resolves <see cref="HealthCheckService"/>, which throws "Duplicate health checks were
/// registered with the name(s): …" — a hard startup failure. That is easy to hit once more than one
/// library registers checks for shared infrastructure, since two packages both wiring up a
/// <c>"PostgreSql"</c> check is a reasonable thing for them to do independently.
/// </remarks>
public static class HealthCheckRegistrationExtensions
{
    /// <summary>
    /// Runs <paramref name="addHealthCheck"/> unless a check named <paramref name="name"/> has
    /// already been added through this method.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The check name being claimed. Must match the name <paramref name="addHealthCheck"/>
    /// registers, since that is the only thing tying the two together.
    /// </param>
    /// <param name="addHealthCheck">Registers the check. Not invoked if the name is already claimed.</param>
    /// <returns>
    /// <see langword="true"/> if the check was registered, <see langword="false"/> if the name was
    /// already claimed.
    /// </returns>
    /// <remarks>
    /// Only tracks names claimed through this method. A check registered directly on
    /// <c>AddHealthChecks()</c> is invisible here and will still collide — first-registration-wins
    /// is a convention libraries opt into, not something that can be enforced retroactively.
    /// </remarks>
    public static bool TryAddHealthCheck(
        this IServiceCollection services,
        string name,
        Action<IHealthChecksBuilder> addHealthCheck)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(addHealthCheck);

        if (!GetClaimedNames(services).Add(name))
        {
            return false;
        }

        addHealthCheck(services.AddHealthChecks());
        return true;
    }

    /// <summary>
    /// Runs <paramref name="addHealthCheck"/> unless a check named <paramref name="name"/> has
    /// already been added through this method.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="name">The check name being claimed.</param>
    /// <param name="addHealthCheck">Registers the check. Not invoked if the name is already claimed.</param>
    /// <returns>
    /// <see langword="true"/> if the check was registered, <see langword="false"/> if the name was
    /// already claimed.
    /// </returns>
    public static bool TryAddHealthCheck(
        this IHostApplicationBuilder builder,
        string name,
        Action<IHealthChecksBuilder> addHealthCheck)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Services.TryAddHealthCheck(name, addHealthCheck);
    }

    // The claim set has to live on the service collection rather than in a static, because
    // registration happens per collection and the same process can build several. Stashing it as an
    // instance descriptor keeps it readable at registration time, which is the only time it matters
    // - it is never resolved from the built container.
    private static HashSet<string> GetClaimedNames(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(ClaimedHealthCheckNames))
            {
                return ((ClaimedHealthCheckNames)services[i].ImplementationInstance!).Names;
            }
        }

        var claimed = new ClaimedHealthCheckNames();
        services.AddSingleton(claimed);
        return claimed.Names;
    }

    private sealed class ClaimedHealthCheckNames
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
