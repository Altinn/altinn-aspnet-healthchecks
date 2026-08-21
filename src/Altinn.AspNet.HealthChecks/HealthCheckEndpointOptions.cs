using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Configures the endpoints mapped by
/// <see cref="HealthCheckEndpointRouteBuilderExtensions.MapAltinnHealthChecks(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, HealthCheckEndpointOptions)"/>.
/// The default <c>/alive</c> and <c>/health</c> paths match the Microsoft/Aspire service-defaults
/// scaffolding, so the endpoints Kubernetes probes are where an Altinn deployment already expects
/// them.
/// </summary>
/// <remarks>
/// Build one instance and pass it to both <c>MapAltinnHealthChecks</c> and
/// <c>AddHealthCheckActivityFilter</c> (from the <c>Altinn.AspNet.HealthChecks.OpenTelemetry</c>
/// package) so customised paths cannot drift out of sync with trace suppression.
/// </remarks>
public sealed class HealthCheckEndpointOptions
{
    /// <summary>Liveness probe. Includes checks tagged <see cref="HealthCheckTags.Live"/>.</summary>
    public HealthEndpoint Liveness { get; } = new("/alive");

    /// <summary>
    /// Readiness probe. Includes checks tagged <see cref="HealthCheckTags.Critical"/>
    /// or <see cref="HealthCheckTags.Warmup"/>.
    /// </summary>
    public HealthEndpoint Readiness { get; } = new("/health/readiness");

    /// <summary>Startup probe. Includes checks tagged <see cref="HealthCheckTags.Dependencies"/>.</summary>
    public HealthEndpoint Startup { get; } = new("/health/startup");

    /// <summary>
    /// Default human/dashboard endpoint. Includes checks tagged
    /// <see cref="HealthCheckTags.Dependencies"/> — the same set as <see cref="Startup"/>.
    /// </summary>
    public HealthEndpoint Health { get; } = new("/health");

    /// <summary>
    /// Deep endpoint. Includes checks tagged <see cref="HealthCheckTags.Dependencies"/> or
    /// <see cref="HealthCheckTags.External"/> (adds the outbound probes).
    /// </summary>
    public HealthEndpoint Deep { get; } = new("/health/deep");

    /// <summary>
    /// How much of each report reaches the response body. Leave <see langword="null"/> — the
    /// default — to derive it from <see cref="IHostEnvironment"/>:
    /// <list type="bullet">
    /// <item><description>Development → <see cref="HealthReportDetailLevel.Full"/></description></item>
    /// <item><description>Production → <see cref="HealthReportDetailLevel.Summary"/></description></item>
    /// <item><description>anything else (Staging, at22, …) → <see cref="HealthReportDetailLevel.Diagnostic"/></description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The environment is read when the endpoints are mapped. Where no
    /// <see cref="IHostEnvironment"/> is registered the fallback is
    /// <see cref="HealthReportDetailLevel.Summary"/> — health endpoints leak, so the unknown case
    /// resolves to the quiet one. Set this explicitly to override, most often to loosen a
    /// production endpoint that sits behind <c>RequireHost</c> or <c>RequireAuthorization</c>.
    /// A value outside the declared levels is rejected when the endpoints are mapped, rather than
    /// clearing every gate and behaving as <see cref="HealthReportDetailLevel.Full"/>.
    /// </remarks>
    public HealthReportDetailLevel? DetailLevel { get; set; }

    /// <summary>
    /// The response formatters, in preference order. The first is used when a request expresses no
    /// usable preference — no <c>Accept</c> header, <c>*/*</c>, or nothing this list can satisfy.
    /// Defaults to JSON then plain text.
    /// </summary>
    /// <remarks>
    /// Mutate to change the outcome: <c>Formatters.Insert(0, myFormatter)</c> to prefer your own
    /// format, <c>Formatters.RemoveAt(1)</c> to stop answering <c>text/plain</c> at all. A
    /// formatter appended at the end still serves its own media type when a client asks for it by
    /// name; only the no-preference case follows the order.
    /// </remarks>
    public IList<HealthReportFormatter> Formatters { get; } =
    [
        HealthReportJsonFormatter.Instance,
        HealthReportTextFormatter.Instance,
    ];

    /// <summary>
    /// Whether the mapped endpoints are excluded from ASP.NET Core's HTTP request metrics.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kubernetes probes every endpoint here every few seconds forever. Left in, they dominate
    /// <c>http.server.request.duration</c> and the request-rate counters without ever saying
    /// anything about how the app is serving real traffic.
    /// </para>
    /// <para>
    /// This is the metrics half of the story; trace spans are suppressed separately by
    /// <c>AddHealthCheckActivityFilter</c> in the <c>Altinn.AspNet.HealthChecks.OpenTelemetry</c>
    /// package. No effect on net8.0, where <c>DisableHttpMetrics</c> does not exist and the
    /// metrics middleware would not honour it.
    /// </para>
    /// </remarks>
    public bool DisableHttpMetrics { get; set; } = true;

    /// <summary>
    /// All five endpoints, in declaration order. Useful for deriving other configuration from the
    /// mapped layout — the OpenTelemetry companion package uses it to suppress trace spans for
    /// exactly the endpoints that are mapped.
    /// </summary>
    public IEnumerable<HealthEndpoint> All
    {
        get
        {
            yield return Liveness;
            yield return Readiness;
            yield return Startup;
            yield return Health;
            yield return Deep;
        }
    }
}

/// <summary>
/// One mapped health endpoint: where it lives, and how the route is configured.
/// </summary>
/// <param name="defaultPath">The convention's default path for this endpoint.</param>
public sealed class HealthEndpoint(string defaultPath)
{
    /// <summary>
    /// The route path. Set to <see langword="null"/> or blank (or call <see cref="Disable"/>) to
    /// leave this endpoint unmapped — useful when a platform probes only a subset, or when an
    /// endpoint should not be exposed at all.
    /// </summary>
    public string? Path { get; set; } = defaultPath;

    /// <summary>
    /// Applied to the mapped endpoint, for route conventions such as
    /// <c>RequireHost("localhost")</c> or <c>RequireAuthorization()</c>. Ignored when
    /// <see cref="Path"/> is <see langword="null"/>.
    /// </summary>
    public Action<IEndpointConventionBuilder>? Configure { get; set; }

    /// <summary>Leaves this endpoint unmapped.</summary>
    public void Disable() => Path = null;
}
