namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Configures the route paths mapped by
/// <see cref="HealthCheckEndpointRouteBuilderExtensions.MapAltinnHealthChecks"/>.
/// Defaults reproduce the Dialogporten endpoint layout, so
/// <c>{path}/health/deep</c> is structurally identical to the reference deployment.
/// </summary>
public sealed class HealthCheckEndpointOptions
{
    /// <summary>Liveness probe path. Filters checks tagged <see cref="HealthCheckTags.Self"/>.</summary>
    public string LivenessPath { get; set; } = "/health/liveness";

    /// <summary>
    /// Readiness probe path. Filters checks tagged <see cref="HealthCheckTags.Critical"/>
    /// or <see cref="HealthCheckTags.Warmup"/>.
    /// </summary>
    public string ReadinessPath { get; set; } = "/health/readiness";

    /// <summary>
    /// Startup probe path. Filters checks tagged <see cref="HealthCheckTags.Dependencies"/>.
    /// </summary>
    public string StartupPath { get; set; } = "/health/startup";

    /// <summary>
    /// Default human/dashboard health path. Filters checks tagged
    /// <see cref="HealthCheckTags.Dependencies"/>.
    /// </summary>
    public string HealthPath { get; set; } = "/health";

    /// <summary>
    /// Deep health path. Filters checks tagged <see cref="HealthCheckTags.Dependencies"/>
    /// or <see cref="HealthCheckTags.External"/> (adds the outbound HTTP checks).
    /// </summary>
    public string DeepPath { get; set; } = "/health/deep";
}
