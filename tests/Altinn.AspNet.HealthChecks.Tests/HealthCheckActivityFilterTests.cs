using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

public sealed class HealthCheckActivityFilterTests : IDisposable
{
    // Matches the prefix the filter looks for; ASP.NET Core server spans come from
    // "Microsoft.AspNetCore.Hosting".
    private const string AspNetCoreSourceName = "Microsoft.AspNetCore.Hosting";

    private readonly ActivitySource _aspNetCoreSource = new(AspNetCoreSourceName);
    private readonly ActivitySource _otherSource = new("Some.Other.Source");
    private readonly CollectingExporter _exporter = new();
    private TracerProvider? _provider;

    public void Dispose()
    {
        _provider?.Dispose();
        _aspNetCoreSource.Dispose();
        _otherSource.Dispose();
    }

    private void BuildProvider(params string[] suppressedRouteSuffixes) =>
        _provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(AspNetCoreSourceName, _otherSource.Name)
            .SetSampler(new AlwaysOnSampler())
            .AddHealthCheckActivityFilter(suppressedRouteSuffixes)
            // The exporter is registered after the filter, which is the documented order.
            .AddProcessor(new SimpleActivityExportProcessor(_exporter))
            .Build();

    private static void EmitServerSpan(ActivitySource source, string tagKey, string route)
    {
        using var activity = source.StartActivity("HTTP GET", ActivityKind.Server);
        Assert.NotNull(activity);
        activity.SetTag(tagKey, route);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/liveness")]
    [InlineData("/health/readiness")]
    [InlineData("/health/startup")]
    [InlineData("/health/deep")]
    [InlineData("/HEALTH/DEEP")]
    [InlineData("/some/path/base/health")]
    public void Health_route_spans_are_not_exported(string route)
    {
        BuildProvider();

        EmitServerSpan(_aspNetCoreSource, "http.route", route);

        Assert.Empty(_exporter.Exported);
    }

    [Fact]
    public void Health_spans_matched_on_url_path_are_not_exported()
    {
        BuildProvider();

        EmitServerSpan(_aspNetCoreSource, "url.path", "/health");

        Assert.Empty(_exporter.Exported);
    }

    [Theory]
    [InlineData("/api/orders")]
    [InlineData("/healthy-ish")]
    public void Other_route_spans_are_exported(string route)
    {
        BuildProvider();

        EmitServerSpan(_aspNetCoreSource, "http.route", route);

        Assert.Single(_exporter.Exported);
    }

    [Fact]
    public void Spans_without_a_route_are_exported()
    {
        BuildProvider();

        using (var activity = _aspNetCoreSource.StartActivity("HTTP GET", ActivityKind.Server))
        {
            Assert.NotNull(activity);
        }

        Assert.Single(_exporter.Exported);
    }

    [Fact]
    public void Spans_from_other_sources_are_left_alone()
    {
        BuildProvider();

        EmitServerSpan(_otherSource, "http.route", "/health");

        Assert.Single(_exporter.Exported);
    }

    [Fact]
    public void Custom_suffixes_replace_the_defaults()
    {
        BuildProvider("/internal/health");

        EmitServerSpan(_aspNetCoreSource, "http.route", "/internal/health");
        Assert.Empty(_exporter.Exported);

        EmitServerSpan(_aspNetCoreSource, "http.route", "/health");
        Assert.Single(_exporter.Exported);
    }

    private sealed class CollectingExporter : BaseExporter<Activity>
    {
        public List<Activity> Exported { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                Exported.Add(activity);
            }

            return ExportResult.Success;
        }
    }
}
