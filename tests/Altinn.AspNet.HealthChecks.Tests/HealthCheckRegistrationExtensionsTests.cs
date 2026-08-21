using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Altinn.AspNet.HealthChecks.Tests;

public sealed class HealthCheckRegistrationExtensionsTests
{
    [Fact]
    public void Registers_the_check_the_first_time()
    {
        var services = new ServiceCollection();

        Assert.True(services.TryAddHealthCheck("postgres", Adds("postgres")));

        Assert.Equal(["postgres"], RegisteredNames(services));
    }

    [Fact]
    public void Skips_a_name_already_claimed()
    {
        var services = new ServiceCollection();

        Assert.True(services.TryAddHealthCheck("postgres", Adds("postgres")));
        Assert.False(services.TryAddHealthCheck("postgres", Adds("postgres")));

        // Registering it twice would make resolving HealthCheckService throw while the endpoints
        // are being mapped — a hard startup failure, not a per-request one.
        Assert.Equal(["postgres"], RegisteredNames(services));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var services = new ServiceCollection();

        Assert.True(services.TryAddHealthCheck("PostgreSql", Adds("PostgreSql")));
        Assert.False(services.TryAddHealthCheck("postgresql", Adds("postgresql")));
    }

    [Fact]
    public void Different_names_both_register()
    {
        var services = new ServiceCollection();

        Assert.True(services.TryAddHealthCheck("postgres", Adds("postgres")));
        Assert.True(services.TryAddHealthCheck("redis", Adds("redis")));

        Assert.Equal(["postgres", "redis"], RegisteredNames(services).OrderBy(name => name));
    }

    [Fact]
    public void Does_not_invoke_the_callback_when_the_name_is_claimed()
    {
        var services = new ServiceCollection();
        var invocations = 0;

        for (var i = 0; i < 3; i++)
        {
            services.TryAddHealthCheck("postgres", builder =>
            {
                invocations++;
                Adds("postgres")(builder);
            });
        }

        Assert.Equal(1, invocations);
    }

    [Fact]
    public void Repeated_convention_registration_does_not_duplicate_the_self_check()
    {
        var services = new ServiceCollection();

        services.AddAltinnHealthChecks();
        services.AddAltinnHealthChecks();

        Assert.Equal(["self"], RegisteredNames(services));
    }

    private static Action<IHealthChecksBuilder> Adds(string name) =>
        builder => builder.AddCheck(name, () => HealthCheckResult.Healthy());

    // The registrations only exist as IConfigureOptions callbacks until the container is built, so
    // this resolves the options the same way HealthCheckService does.
    private static IEnumerable<string> RegisteredNames(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Select(registration => registration.Name);
    }
}
