using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Sw5e.Infrastructure.Persistence.Moderation;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Which database the moderation schema lands in.
/// </summary>
/// <remarks>
/// <para>
/// This looks like a test of a small helper and is not. The API resolves this
/// connection to decide where a report is written, and the migrator resolves it
/// to decide where the schema is created. If the two ever answer differently,
/// the deployment succeeds, the schema exists, every health probe is green, and
/// the first person to report a wrong picture gets a 500 — which is exactly the
/// class of failure that only shows up in production.
/// </para>
/// <para>
/// So the precedence is pinned here, in order, including the case that makes it
/// unusual: falling back to the identity connection.
/// </para>
/// </remarks>
public sealed class ModerationRegistrationTests
{
    private const string Dedicated =
        "Host=moderation.invalid;Database=sw5e;Username=moderation;Password=moderation-only";

    private const string Platform =
        "Host=content.invalid;Database=sw5e;Username=content;Password=content-only";

    private const string Identity =
        "Host=identity.invalid;Database=sw5e_identity;Username=identity;Password=identity-only";

    [Fact]
    public void TheDedicatedSettingWins()
    {
        var configuration = Configuration(
            ("Moderation:ConnectionString", Dedicated),
            ("ConnectionStrings:Sw5eModeration", "Host=wrong.invalid;Database=a;Username=b"),
            ("ConnectionStrings:Sw5e", Platform),
            ("ConnectionStrings:Sw5eIdentity", Identity));

        ModerationServiceCollectionExtensions.ResolveConnectionString(configuration)
            .ShouldBe(Dedicated);
    }

    [Fact]
    public void ANamedModerationConnectionBeatsThePlatformOne()
    {
        var configuration = Configuration(
            ("ConnectionStrings:Sw5eModeration", Dedicated),
            ("ConnectionStrings:Sw5e", Platform),
            ("ConnectionStrings:Sw5eIdentity", Identity));

        ModerationServiceCollectionExtensions.ResolveConnectionString(configuration)
            .ShouldBe(Dedicated);
    }

    [Fact]
    public void ThePlatformConnectionIsTheOrdinaryAnswer()
    {
        // The single-database deployment, which is what everything runs today:
        // three schemas, three migration histories, one connection string.
        var configuration = Configuration(
            ("ConnectionStrings:Sw5e", Platform),
            ("ConnectionStrings:Sw5eIdentity", Identity));

        ModerationServiceCollectionExtensions.ResolveConnectionString(configuration)
            .ShouldBe(Platform);
    }

    [Fact]
    public void TheIdentityConnectionIsTheLastResort()
    {
        // A deployment serving content from JSON files rather than from
        // PostgreSQL has no reason to set ConnectionStrings__Sw5e at all — the
        // site's own container smoke test is one. Refusing to start there would
        // mean the arrival of flagging broke a configuration that has nothing
        // to do with it, and accounts exist in every deployment.
        var configuration = Configuration(("ConnectionStrings:Sw5eIdentity", Identity));

        ModerationServiceCollectionExtensions.ResolveConnectionString(configuration)
            .ShouldBe(Identity);
    }

    [Fact]
    public void NothingConfiguredIsAStartupFailure()
    {
        // Loudly, at registration, rather than on the first report. A store
        // that is only discovered to be missing when somebody tries to use it
        // is a store that is missing in production.
        var exception = Should.Throw<InvalidOperationException>(
            () => ModerationServiceCollectionExtensions.ResolveConnectionString(
                new ConfigurationBuilder().Build()));

        exception.Message.ShouldContain("ConnectionStrings__Sw5eModeration");
    }

    [Fact]
    public void TheContextIsRegisteredScopedAndOpensNoConnection()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSw5eModeration(Configuration(("ConnectionStrings:Sw5e", Platform)));

        var descriptor = services.Single(
            candidate => candidate.ServiceType == typeof(Sw5eModerationDbContext));

        descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);

        // Resolving the context composes the object graph and nothing more. A
        // registration that connected eagerly would make a briefly unavailable
        // moderation database into a deployment that refuses to start, and the
        // reference this site exists to publish does not come from it.
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<Sw5eModerationDbContext>().ShouldNotBeNull();
    }

    /// <summary>
    /// Registering moderation must not register a health check.
    /// </summary>
    /// <remarks>
    /// Asserted rather than left as a comment, because adding one is the
    /// obvious next thing somebody does and the consequence is severe:
    /// <c>/health/ready</c> would report the whole deployment unhealthy — and a
    /// load balancer would take it out of rotation — because nobody can file a
    /// typo report. The reference is served from an entirely different store.
    /// </remarks>
    [Fact]
    public void NoHealthCheckIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSw5eModeration(Configuration(("ConnectionStrings:Sw5e", Platform)));

        services
            .Any(descriptor => (descriptor.ServiceType.FullName ?? string.Empty)
                .Contains("HealthCheck", StringComparison.Ordinal))
            .ShouldBeFalse();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key,
                setting => (string?)setting.Value))
            .Build();
}
