namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// The health check tags used to route registered checks onto the different endpoints.
/// Register your own <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck"/>
/// instances with these tags to have them surface on the corresponding endpoint(s).
/// </summary>
public static class HealthCheckTags
{
    /// <summary>Process-only check. Surfaces on the liveness endpoint. Should always be healthy.</summary>
    public const string Self = "self";

    /// <summary>
    /// A dependency the app talks to (database, cache, broker, ...). Surfaces on the
    /// default, startup and deep endpoints.
    /// </summary>
    public const string Dependencies = "dependencies";

    /// <summary>
    /// A dependency without which the app cannot serve traffic. Surfaces on the readiness
    /// endpoint so an unhealthy instance is de-pooled.
    /// </summary>
    public const string Critical = "critical";

    /// <summary>
    /// Startup warmup gate. Surfaces on the readiness endpoint. See <c>AddWarmup</c> in the
    /// <c>Altinn.AspNet.HealthChecks.Warmup</c> companion package.
    /// </summary>
    public const string Warmup = "warmup";

    /// <summary>
    /// An outbound/external check that is only exercised on the deep endpoint (e.g. an
    /// <c>AddUrlGroup</c> probe of an upstream service's well-known endpoint).
    /// </summary>
    public const string External = "external";
}
