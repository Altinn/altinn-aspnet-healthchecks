using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

/// <summary>
/// How the detail level is derived when the app does not set one. This is the switch that decides
/// whether a production body carries connection strings, so it is tested directly rather than only
/// through the endpoints.
/// </summary>
public sealed class HealthReportDetailLevelTests
{
    [Theory]
    [InlineData("Development", HealthReportDetailLevel.Full)]
    [InlineData("Production", HealthReportDetailLevel.Summary)]
    [InlineData("Staging", HealthReportDetailLevel.Diagnostic)]
    // Altinn's test environments are named after the cluster, and must not be mistaken for
    // production-like or development-like.
    [InlineData("at22", HealthReportDetailLevel.Diagnostic)]
    [InlineData("yt01", HealthReportDetailLevel.Diagnostic)]
    public void Derives_from_the_environment(string environmentName, HealthReportDetailLevel expected)
    {
        var resolved = HealthCheckEndpointRouteBuilderExtensions.ResolveDetailLevel(
            configured: null,
            Services(environmentName));

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Falls_back_to_summary_without_a_host_environment()
    {
        // A bare IEndpointRouteBuilder outside a host is a legitimate way to map these endpoints.
        // Health endpoints leak, so the unknown case resolves to the quiet level.
        var resolved = HealthCheckEndpointRouteBuilderExtensions.ResolveDetailLevel(
            configured: null,
            new ServiceCollection().BuildServiceProvider());

        Assert.Equal(HealthReportDetailLevel.Summary, resolved);
    }

    [Fact]
    public void An_explicit_level_wins_over_the_environment()
    {
        var resolved = HealthCheckEndpointRouteBuilderExtensions.ResolveDetailLevel(
            HealthReportDetailLevel.Minimal,
            Services("Development"));

        Assert.Equal(HealthReportDetailLevel.Minimal, resolved);
    }

    private static ServiceProvider Services(string environmentName) =>
        new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new StubEnvironment { EnvironmentName = environmentName })
            .BuildServiceProvider();

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
