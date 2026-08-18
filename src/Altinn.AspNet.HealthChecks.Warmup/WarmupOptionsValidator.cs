using Microsoft.Extensions.Options;

namespace Altinn.AspNet.HealthChecks.Warmup;

/// <summary>
/// Validates <see cref="WarmupOptions"/> at host startup (via <c>ValidateOnStart</c>), so a
/// misconfigured timeout is a clear boot failure.
/// </summary>
/// <remarks>
/// Validating here rather than inside the hosted service matters: an invalid timeout discovered
/// mid-run would fault the background task, leaving readiness stuck Pending with no explanation.
/// </remarks>
internal sealed class WarmupOptionsValidator : IValidateOptions<WarmupOptions>
{
    /// <summary>
    /// Upper bound on any warmup timeout, in seconds (one hour). Generous for real warmup work,
    /// while still catching the transposed digit that would otherwise hold readiness at 503 for
    /// weeks without a single log line.
    /// </summary>
    public const int MaxTimeoutSeconds = 3600;

    public ValidateOptionsResult Validate(string? name, WarmupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Disabled warmup runs no phases, so none of this configuration is consulted. Validating
        // it anyway would let a bad timeout block startup on a subsystem that is switched off —
        // defeating the kill switch precisely when someone reaches for it.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        Validate($"{nameof(WarmupOptions)}.{nameof(WarmupOptions.TimeoutSeconds)}", options.TimeoutSeconds, failures);

        foreach (var phase in options.Phases)
        {
            if (phase.TimeoutSeconds is { } phaseTimeout)
            {
                Validate($"Phase '{phase.Name}' {nameof(IWarmupPhase.TimeoutSeconds)}", phaseTimeout, failures);
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void Validate(string label, int seconds, List<string> failures)
    {
        if (seconds is < 1 or > MaxTimeoutSeconds)
        {
            failures.Add($"{label} must be between 1 and {MaxTimeoutSeconds} seconds, but was {seconds}.");
        }
    }
}
