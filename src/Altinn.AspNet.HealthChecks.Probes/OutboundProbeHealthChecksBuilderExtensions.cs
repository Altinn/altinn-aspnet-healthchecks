using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Altinn.AspNet.HealthChecks.Probes;

/// <summary>
/// Registers outbound HTTP probes of upstream services, tagged
/// <see cref="HealthCheckTags.External"/> so they run only on the deep endpoint.
/// </summary>
public static class OutboundProbeHealthChecksBuilderExtensions
{
    /// <summary>
    /// Registers one probe per entry in <paramref name="configuration"/>.
    /// </summary>
    /// <param name="builder">The health checks builder, e.g. from <c>AddAltinnHealthChecks()</c>.</param>
    /// <param name="configuration">
    /// A configuration section binding to an array of <see cref="OutboundProbe"/>, supplied by the
    /// caller — the library never invents a key name, since an Altinn app's configuration may come
    /// from Azure App Configuration rather than any appsettings file.
    /// </param>
    /// <param name="configure">Configures base URI, timeout and extra tags.</param>
    /// <exception cref="InvalidOperationException">
    /// A probe is invalid (missing name, neither or both of URL/relative path, a relative path
    /// without a base URI) or two probes share a name. The message names the offending
    /// configuration path.
    /// </exception>
    /// <example>
    /// <code>
    /// services.AddAltinnHealthChecks()
    ///     .AddOutboundProbes(configuration.GetSection("HealthProbes"), probes =>
    ///     {
    ///         probes.BaseUri = new Uri("https://platform.tt02.altinn.no/");
    ///         probes.Timeout = TimeSpan.FromSeconds(10);
    ///     });
    /// </code>
    /// with
    /// <code>
    /// { "HealthProbes": [
    ///     { "Name": "Access Management", "RelativePath": "accessmanagement/api/v1/meta/info", "Hard": true },
    ///     { "Name": "PDP", "Url": "https://pdp.example.no/health" }
    /// ]}
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddOutboundProbes(
        this IHealthChecksBuilder builder,
        IConfiguration configuration,
        Action<OutboundProbeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = CreateOptions(configure);

        // Bound manually rather than via Get<List<OutboundProbe>>() so each failure can name the
        // exact configuration path that caused it — "HealthProbes:2" beats "one of your probes".
        // An empty or missing section registers nothing and does not throw: an environment may
        // legitimately configure no probes.
        foreach (var child in configuration.GetChildren())
        {
            var probe = child.Get<OutboundProbe>()
                ?? throw new InvalidOperationException($"Health probe at '{child.Path}' could not be bound.");

            builder.AddOutboundProbe(probe, options, child.Path);
        }

        return builder;
    }

    /// <summary>
    /// Registers a single probe from code, for upstreams that are not configuration-driven.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check name. Must be unique.</param>
    /// <param name="url">The absolute URL to probe.</param>
    /// <param name="hard">
    /// Whether the app is broken without this upstream: hard reports <c>Unhealthy</c>, soft
    /// reports <c>Degraded</c>. Defaults to <see langword="false"/>.
    /// </param>
    /// <param name="configure">Configures timeout and extra tags. <c>BaseUri</c> is unused here.</param>
    public static IHealthChecksBuilder AddOutboundProbe(
        this IHealthChecksBuilder builder,
        string name,
        Uri url,
        bool hard = false,
        Action<OutboundProbeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(url);

        var options = CreateOptions(configure);
        var probe = new OutboundProbe { Name = name, Url = url.ToString(), Hard = hard };
        return builder.AddOutboundProbe(probe, options, configurationPath: null);
    }

    private static OutboundProbeOptions CreateOptions(Action<OutboundProbeOptions>? configure)
    {
        var options = new OutboundProbeOptions();
        configure?.Invoke(options);

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(OutboundProbeOptions)}.{nameof(OutboundProbeOptions.Timeout)} must be greater than zero, but was {options.Timeout}.");
        }

        return options;
    }

    private static IHealthChecksBuilder AddOutboundProbe(
        this IHealthChecksBuilder builder,
        OutboundProbe probe,
        OutboundProbeOptions options,
        string? configurationPath)
    {
        var origin = configurationPath is null ? $"Probe '{probe.Name}'" : $"Health probe at '{configurationPath}'";
        var uri = ResolveUri(probe, options, origin);

        if (string.IsNullOrWhiteSpace(probe.Name))
        {
            throw new InvalidOperationException($"{origin} has no {nameof(OutboundProbe.Name)}.");
        }

        // Health check names must be unique: a duplicate makes MapAltinnHealthChecks throw while
        // mapping, which is a hard startup failure naming only the check. Catching it here lets
        // the message name the configuration entry that introduced it.
        if (ExistingCheckNames(builder.Services).Contains(probe.Name))
        {
            throw new InvalidOperationException(
                $"{origin} duplicates the name '{probe.Name}' of an already-registered health check. " +
                "Health check names must be unique.");
        }

        string[] tags = [HealthCheckTags.External, .. options.Tags];

        builder.AddUrlGroup(
            uri,
            name: probe.Name,
            failureStatus: probe.Hard ? HealthStatus.Unhealthy : HealthStatus.Degraded,
            tags: tags,
            timeout: options.Timeout);

        return builder;
    }

    private static Uri ResolveUri(OutboundProbe probe, OutboundProbeOptions options, string origin)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(probe.Url);
        var hasRelativePath = !string.IsNullOrWhiteSpace(probe.RelativePath);

        if (hasUrl == hasRelativePath)
        {
            throw new InvalidOperationException(
                $"{origin} must set exactly one of {nameof(OutboundProbe.Url)} and {nameof(OutboundProbe.RelativePath)}, " +
                (hasUrl ? "but sets both." : "but sets neither."));
        }

        if (hasUrl)
        {
            return Uri.TryCreate(probe.Url, UriKind.Absolute, out var absolute)
                ? absolute
                : throw new InvalidOperationException($"{origin} has {nameof(OutboundProbe.Url)} '{probe.Url}', which is not an absolute URI.");
        }

        var relativePath = probe.RelativePath!;

        // Uri.TryCreate(base, relative) happily returns an absolute value verbatim and resolves a
        // leading '/' against the authority, discarding any path on the base URI. Either would
        // silently defeat the point of RelativePath: a tt02 deployment could probe production, or
        // a base of https://host/platform/ could lose its /platform segment. Both are rejected
        // rather than resolved, because both look correct in config review.
        // Leading-slash first: on Unix a rooted path like "/am/health" is itself a well-formed
        // absolute file URI, so the absolute check below would otherwise claim it and report the
        // less useful of the two diagnoses.
        if (relativePath.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"{origin} has {nameof(OutboundProbe.RelativePath)} '{relativePath}', which starts with '/' and would " +
                $"replace any path in {nameof(OutboundProbeOptions.BaseUri)} rather than extend it. Remove the leading slash.");
        }

        if (Uri.TryCreate(relativePath, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"{origin} has {nameof(OutboundProbe.RelativePath)} '{relativePath}', which is an absolute URI. " +
                $"Use {nameof(OutboundProbe.Url)} for absolute addresses, or make the path relative to {nameof(OutboundProbeOptions.BaseUri)}.");
        }

        if (options.BaseUri is not { } baseUri)
        {
            throw new InvalidOperationException(
                $"{origin} uses {nameof(OutboundProbe.RelativePath)}, but no {nameof(OutboundProbeOptions.BaseUri)} was configured.");
        }

        return Uri.TryCreate(baseUri, relativePath, out var resolved)
            ? resolved
            : throw new InvalidOperationException(
                $"{origin} has {nameof(OutboundProbe.RelativePath)} '{relativePath}', which does not resolve against base URI '{baseUri}'.");
    }

    /// <summary>
    /// Names of every health check registered on <paramref name="services"/> so far.
    /// </summary>
    /// <remarks>
    /// <c>AddCheck</c> registers through <c>Configure&lt;HealthCheckServiceOptions&gt;</c>, whose
    /// delegates are only readable by running them. Running them against a throwaway options
    /// instance is safe — they do nothing but append registrations to the list they are handed —
    /// and it is the only way to see names owned by other packages (<c>self</c>, <c>warmup</c>)
    /// or by the app's own <c>AddCheck</c> calls. Best-effort by nature: checks registered
    /// *after* this call are invisible, and are caught by the framework at mapping instead.
    /// </remarks>
    private static HashSet<string> ExistingCheckNames(IServiceCollection services)
    {
        var probeOptions = new HealthCheckServiceOptions();

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigureOptions<HealthCheckServiceOptions>) &&
                descriptor.ImplementationInstance is IConfigureOptions<HealthCheckServiceOptions> configure)
            {
                configure.Configure(probeOptions);
            }
        }

        return probeOptions.Registrations.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
