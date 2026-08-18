using Altinn.AspNet.HealthChecks.Probes;
using Altinn.AspNet.HealthChecks.Warmup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

public class OutboundProbeTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static IHealthChecksBuilder NewBuilder() => new ServiceCollection().AddAltinnHealthChecks();

    private static List<HealthCheckRegistration> RegistrationsOf(IHealthChecksBuilder builder) =>
        builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToList();

    [Fact]
    public void Relative_path_resolves_against_base_uri()
    {
        var builder = NewBuilder();

        builder.AddOutboundProbes(
            Config(("Probes:0:Name", "Access Management"), ("Probes:0:RelativePath", "accessmanagement/api/v1/meta/info"))
                .GetSection("Probes"),
            o => o.BaseUri = new Uri("https://platform.example.no/"));

        var registration = Assert.Single(RegistrationsOf(builder), r => r.Name == "Access Management");
        Assert.Contains(HealthCheckTags.External, registration.Tags);
    }

    [Fact]
    public void Hard_probe_fails_unhealthy_and_soft_probe_degrades()
    {
        var builder = NewBuilder();

        builder.AddOutboundProbes(
            Config(
                ("Probes:0:Name", "hard"), ("Probes:0:Url", "https://example.com/a"), ("Probes:0:Hard", "true"),
                ("Probes:1:Name", "soft"), ("Probes:1:Url", "https://example.com/b"))
                .GetSection("Probes"));

        var registrations = RegistrationsOf(builder);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(registrations, r => r.Name == "hard").FailureStatus);
        Assert.Equal(HealthStatus.Degraded, Assert.Single(registrations, r => r.Name == "soft").FailureStatus);
    }

    [Fact]
    public void Duplicate_probe_names_are_rejected_naming_the_config_path()
    {
        var builder = NewBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddOutboundProbes(
            Config(
                ("Probes:0:Name", "Altinn"), ("Probes:0:Url", "https://example.com/a"),
                ("Probes:1:Name", "Altinn"), ("Probes:1:Url", "https://example.com/b"))
                .GetSection("Probes")));

        // The framework's own duplicate error names only the check; ours names the entry that
        // introduced it, which is what makes it actionable.
        Assert.Contains("Probes:1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Altinn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_probe_registered_in_code_also_collides()
    {
        var builder = NewBuilder();
        builder.AddOutboundProbe("Maskinporten", new Uri("https://example.com/wk"));

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddOutboundProbes(
            Config(("Probes:0:Name", "Maskinporten"), ("Probes:0:Url", "https://example.com/other"))
                .GetSection("Probes")));

        Assert.Contains("Probes:0", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Neither Url nor RelativePath.
    [InlineData(null, null, "neither")]
    // Both at once.
    [InlineData("https://example.com/a", "some/path", "both")]
    public void Exactly_one_of_url_and_relative_path_is_required(string? url, string? relativePath, string expected)
    {
        var builder = NewBuilder();
        var entries = new List<(string, string)> { ("Probes:0:Name", "probe") };
        if (url is not null)
        {
            entries.Add(("Probes:0:Url", url));
        }

        if (relativePath is not null)
        {
            entries.Add(("Probes:0:RelativePath", relativePath));
        }

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddOutboundProbes(Config([.. entries]).GetSection("Probes"), o => o.BaseUri = new Uri("https://example.com/")));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Probes:0", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Silently probes production from a test deployment: Uri.TryCreate(base, absolute) returns
    // the absolute value verbatim, so BaseUri is ignored and nothing complains.
    [InlineData("https://prod.altinn.no/am/health", "absolute URI")]
    // Resolves against the authority, discarding the base URI's own path segment.
    [InlineData("/am/health", "starts with '/'")]
    public void Relative_path_that_is_not_actually_relative_is_rejected(string relativePath, string expected)
    {
        var builder = NewBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddOutboundProbes(
            Config(("Probes:0:Name", "AM"), ("Probes:0:RelativePath", relativePath)).GetSection("Probes"),
            o => o.BaseUri = new Uri("https://platform.tt02.altinn.no/platform/")));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Probes:0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_path_extends_a_base_uri_path_rather_than_replacing_it()
    {
        var builder = NewBuilder();

        builder.AddOutboundProbes(
            Config(("Probes:0:Name", "AM"), ("Probes:0:RelativePath", "am/health")).GetSection("Probes"),
            o => o.BaseUri = new Uri("https://host.example/platform/"));

        Assert.Single(RegistrationsOf(builder), r => r.Name == "AM");
    }

    [Fact]
    public void A_probe_colliding_with_another_packages_check_is_rejected()
    {
        // "self" comes from AddAltinnHealthChecks, "warmup" from the Warmup package. Neither is
        // visible to a guard that only tracks its own probes, and the collision would otherwise
        // surface as a hard startup crash naming only the check.
        var services = new ServiceCollection();
        var builder = services.AddAltinnHealthChecks();
        services.AddWarmup(warmup => warmup.AddPhase("noop", (_, _) => Task.CompletedTask));

        foreach (var colliding in new[] { "self", "warmup" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => builder.AddOutboundProbes(
                Config(("Probes:0:Name", colliding), ("Probes:0:Url", "https://example.com/a")).GetSection("Probes")));

            Assert.Contains(colliding, ex.Message, StringComparison.Ordinal);
            Assert.Contains("Probes:0", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Relative_path_without_base_uri_is_rejected()
    {
        var builder = NewBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddOutboundProbes(
            Config(("Probes:0:Name", "probe"), ("Probes:0:RelativePath", "meta/info")).GetSection("Probes")));

        Assert.Contains(nameof(OutboundProbeOptions.BaseUri), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_section_registers_nothing_without_throwing()
    {
        var builder = NewBuilder();
        var before = RegistrationsOf(builder).Count;

        builder.AddOutboundProbes(Config().GetSection("Probes"));

        Assert.Equal(before, RegistrationsOf(builder).Count);
    }
}
