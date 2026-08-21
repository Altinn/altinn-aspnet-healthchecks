namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// How much of a health report is written to the response body. The levels are ordered from least
/// to most revealing, so a formatter can gate a field with <c>level &gt;= X</c>.
/// </summary>
/// <remarks>
/// <para>
/// Health endpoints leak. Exception messages carry connection strings and hostnames; a check's
/// <c>data</c> is whatever that check felt like publishing, and a third-party check may report
/// broker addresses and queue names there while perfectly healthy. So the level is derived from
/// <see cref="Microsoft.Extensions.Hosting.IHostEnvironment"/> by default — see
/// <see cref="HealthCheckEndpointOptions.DetailLevel"/> — and production gets
/// <see cref="Summary"/>.
/// </para>
/// <para>
/// The levels bundle fields that could in principle be toggled independently. That is deliberate:
/// a handful of orthogonal booleans is how the previous design ran out of room. If you need a
/// combination the ladder does not offer, derive from <see cref="HealthReportFormatter"/> and
/// write exactly the fields you want.
/// </para>
/// </remarks>
public enum HealthReportDetailLevel
{
    /// <summary>
    /// Overall status, total duration, and each entry's name, status and duration. Nothing a check
    /// authored itself. Safe to expose to anything.
    /// </summary>
    Minimal = 0,

    /// <summary>
    /// Adds each entry's tags, and its description when the entry carries no exception.
    /// </summary>
    /// <remarks>
    /// The description is withheld from failing entries on purpose. When a check <em>throws</em>,
    /// <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService"/> builds the
    /// entry with the exception message as its description — so the secret is already sitting in
    /// the description before any formatter sees it, and suppressing only the exception field
    /// would publish it anyway. A description is safe to write only when no exception accompanies
    /// it.
    /// </remarks>
    Summary = 1,

    /// <summary>
    /// Adds each entry's <c>data</c> (when non-empty), its exception message, and the description
    /// unconditionally. Enough to diagnose a failure from the response body alone.
    /// </summary>
    Diagnostic = 2,

    /// <summary>
    /// Adds exception stack traces and inner exceptions. Intended for development.
    /// </summary>
    Full = 3,
}
