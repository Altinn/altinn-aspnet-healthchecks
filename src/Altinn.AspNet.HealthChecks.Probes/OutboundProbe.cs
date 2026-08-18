namespace Altinn.AspNet.HealthChecks.Probes;

/// <summary>
/// One outbound HTTP probe, as bound from configuration.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Url"/> and <see cref="RelativePath"/> must be set.
/// </remarks>
public sealed class OutboundProbe
{
    /// <summary>
    /// The probe's health check name, shown in the response body. Must be unique across all
    /// registered health checks.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>An absolute URL to probe. Mutually exclusive with <see cref="RelativePath"/>.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// A path resolved against <see cref="OutboundProbeOptions.BaseUri"/>, so the same
    /// configuration works across environments with only the base URI changing. Mutually
    /// exclusive with <see cref="Url"/>.
    /// </summary>
    public string? RelativePath { get; set; }

    /// <summary>
    /// Whether the app is broken without this upstream. Hard probes report
    /// <c>Unhealthy</c> and fail the deep endpoint; soft probes report <c>Degraded</c> and
    /// leave it at 200. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Hard is not the same axis as the <see cref="HealthCheckTags.Critical"/> tag. Critical
    /// gates <em>readiness</em>, so a critical check failing de-pools the instance; hard only
    /// fails the <em>deep</em> endpoint. Outbound probes are tagged
    /// <see cref="HealthCheckTags.External"/> and should essentially never be critical — an
    /// upstream outage should not take your own pods out of rotation.
    /// </remarks>
    public bool Hard { get; set; }
}
