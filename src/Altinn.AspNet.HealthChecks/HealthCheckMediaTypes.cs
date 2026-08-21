namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// The media types the built-in response formatters produce.
/// </summary>
public static class HealthCheckMediaTypes
{
    /// <summary>
    /// The versioned Altinn health report format, written by <see cref="HealthReportJsonFormatter"/>.
    /// </summary>
    /// <remarks>
    /// A vendor media type rather than plain <c>application/json</c> so the payload shape can be
    /// versioned independently of the package: a future <c>v2</c> ships as a second formatter and
    /// clients opt in through <c>Accept</c>. A client asking for plain <c>application/json</c>
    /// still gets this — a <c>+json</c> type is a subset of it — so nobody has to know the vendor
    /// type exists to read the body.
    /// </remarks>
    public const string Json = "application/vnd.altinn.health.v1+json";

    /// <summary>Single-word status, written by <see cref="HealthReportTextFormatter"/>.</summary>
    public const string PlainText = "text/plain";
}
