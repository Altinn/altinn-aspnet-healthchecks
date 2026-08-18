using Microsoft.AspNetCore.Builder;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Configures the endpoints mapped by
/// <see cref="HealthCheckEndpointRouteBuilderExtensions.MapAltinnHealthChecks(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, HealthCheckEndpointOptions)"/>.
/// Defaults reproduce the Dialogporten endpoint layout, so <c>/health/deep</c> is structurally
/// identical to the reference deployment.
/// </summary>
/// <remarks>
/// Build one instance and pass it to both <c>MapAltinnHealthChecks</c> and
/// <c>AddHealthCheckActivityFilter</c> (from the <c>Altinn.AspNet.HealthChecks.OpenTelemetry</c>
/// package) so customised paths cannot drift out of sync with trace suppression.
/// </remarks>
public sealed class HealthCheckEndpointOptions
{
    /// <summary>Liveness probe. Includes checks tagged <see cref="HealthCheckTags.Self"/>.</summary>
    public HealthEndpoint Liveness { get; } = new("/health/liveness");

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
    /// Whether the response body includes each entry's exception message, and falls back to it
    /// for a missing description. Defaults to <see langword="true"/>, matching
    /// <c>AspNetCore.HealthChecks.UI.Client</c> byte for byte.
    /// </summary>
    /// <remarks>
    /// Exception messages routinely carry connection strings, hostnames and credentials. Set this
    /// to <see langword="false"/> wherever a health endpoint is reachable by anything you do not
    /// trust — a public ingress or API gateway, say — accepting that the body then diverges from
    /// the HealthChecks UI format. A future major version will default this to
    /// <see langword="false"/>.
    /// </remarks>
    public bool IncludeExceptionDetails { get; set; } = true;

    /// <summary>
    /// Whether the response body includes each entry's <c>data</c> contents. Defaults to
    /// <see langword="true"/>, matching <c>AspNetCore.HealthChecks.UI.Client</c>. When
    /// <see langword="false"/>, every entry still carries a <c>data</c> object, but an empty one.
    /// </summary>
    /// <remarks>
    /// A check decides for itself what to put in its data, and a third-party check may put more
    /// there than you would: MassTransit's bus-state check, for example, reports the broker's
    /// host address and every queue name it knows. Unlike <see cref="IncludeExceptionDetails"/>
    /// this is not about failures — the data is published while everything is healthy — so it is a
    /// separate switch, and worth turning off wherever a health endpoint faces something you do
    /// not trust. Keeping the (empty) <c>data</c> object means the body still parses as the
    /// HealthChecks UI format.
    /// </remarks>
    public bool IncludeData { get; set; } = true;

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
