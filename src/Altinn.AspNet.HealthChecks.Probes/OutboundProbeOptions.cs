namespace Altinn.AspNet.HealthChecks.Probes;

/// <summary>
/// Configures how outbound probes are resolved and executed.
/// </summary>
public sealed class OutboundProbeOptions
{
    /// <summary>
    /// Base URI that <see cref="OutboundProbe.RelativePath"/> entries resolve against — typically
    /// the platform base for the current environment. Required only when at least one probe uses
    /// a relative path.
    /// </summary>
    /// <remarks>
    /// Resolution uses <see cref="Uri(Uri, string)"/>, so a base URI without a trailing slash
    /// drops its last segment. <c>https://platform.example.no/api</c> + <c>meta/info</c> resolves
    /// to <c>https://platform.example.no/meta/info</c>, not <c>/api/meta/info</c>. Include the
    /// trailing slash when the base has a path.
    /// </remarks>
    public Uri? BaseUri { get; set; }

    /// <summary>Per-probe HTTP timeout. Defaults to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Extra tags applied to every registered probe, in addition to
    /// <see cref="HealthCheckTags.External"/>.
    /// </summary>
    public IList<string> Tags { get; } = [];
}
