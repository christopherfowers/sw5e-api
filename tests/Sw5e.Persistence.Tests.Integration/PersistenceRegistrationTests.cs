using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Persistence;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// What <see cref="PersistenceServiceCollectionExtensions.AddSw5ePersistence"/>
/// puts in the container, and what it deliberately does not.
/// </summary>
/// <remarks>
/// These need no database: the registrations are decisions, and a decision can
/// be wrong without a server being involved. They are here rather than in a
/// unit-test project because this is where everything that knows about
/// persistence lives.
/// </remarks>
public sealed class PersistenceRegistrationTests
{
    private const string ContentConnection =
        "Host=content.invalid;Database=sw5e;Username=content;Password=content-only";

    private const string IdentityConnection =
        "Host=identity.invalid;Database=sw5e_identity;Username=identity;Password=identity-only";

    /// <summary>
    /// The content data source must not be resolvable as a plain
    /// <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole reason it is keyed. Identity reads its own connection
    /// string so a deployment can give account data a least-privileged role, or
    /// a database of its own. An unkeyed <see cref="NpgsqlDataSource"/>
    /// singleton would sit in the container looking exactly like the thing to
    /// resolve "to share the pool", and the account tables would quietly move
    /// onto the content connection — a change with no error, no log line and no
    /// failing test, discovered only by noticing that a role which should have
    /// no rights over content has them.
    /// </para>
    /// <para>
    /// Asserting that resolution throws is what makes the mistake impossible
    /// rather than merely discouraged. The paired positive case below is what
    /// stops this passing against a method that registered no data source at
    /// all.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePlatformDataSourceIsNotReachableWithoutItsKey()
    {
        using var provider = Build();

        provider.GetService<NpgsqlDataSource>().ShouldBeNull(
            "identity must not be able to pick up the content connection by accident");

        var keyed = provider.GetRequiredKeyedService<NpgsqlDataSource>(
            PersistenceServiceCollectionExtensions.ContentDataSourceKey);

        keyed.ConnectionString.ShouldContain("content.invalid");
    }

    /// <summary>
    /// A second feature configuring its own connection must not be able to
    /// change where content is stored, and vice versa.
    /// </summary>
    /// <remarks>
    /// The identity connection string is present in configuration throughout
    /// this test. If the content store ever resolved its connection through a
    /// fallback chain, or through a shared unkeyed registration that the last
    /// caller won, this is where that would show.
    /// </remarks>
    [Fact]
    public void ContentUsesItsOwnConnectionStringAndNotIdentitys()
    {
        using var provider = Build();

        var factory = provider.GetRequiredService<IDbContextFactory<Sw5eContentDbContext>>();
        using var database = factory.CreateDbContext();

        var connection = database.Database.GetConnectionString();

        connection.ShouldNotBeNull();
        connection!.ShouldContain("content.invalid");
        connection.ShouldNotContain("identity.invalid");
    }

    /// <summary>
    /// Registering persistence twice must be a no-op rather than a duplicate.
    /// </summary>
    /// <remarks>
    /// Content and identity are separate features that each need a database,
    /// and either could grow a call to this method. A second registration of
    /// the health check under the same name throws when the probe first runs,
    /// which is a startup-shaped bug that only appears in a deployed
    /// environment.
    /// </remarks>
    [Fact]
    public void RegisteringPersistenceTwiceIsSafe()
    {
        var services = NewServices();

        services.AddSw5ePersistence(Configuration());
        services.AddSw5ePersistence(Configuration());

        services.Count(descriptor =>
                descriptor.IsKeyedService &&
                descriptor.ServiceType == typeof(NpgsqlDataSource))
            .ShouldBe(1);

        using var provider = services.BuildServiceProvider();

        // The health check registry throws on a duplicate name when it is
        // resolved, so resolving it is the assertion.
        Should.NotThrow(() =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>().Value);
    }

    /// <summary>
    /// A missing connection string is a startup failure, not a lazy one.
    /// </summary>
    /// <remarks>
    /// Deferring it to the first query would mean a deployment configured for
    /// the database store comes up, passes its liveness probe, and answers the
    /// first real request with a 500.
    /// </remarks>
    [Fact]
    public void AMissingConnectionStringFailsAtRegistration()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Should.Throw<InvalidOperationException>(
            () => NewServices().AddSw5ePersistence(configuration));

        exception.Message.ShouldContain("ConnectionStrings__Sw5e");
    }

    /// <summary>
    /// The content store is registered as a singleton, which is what the
    /// interface's own remarks require.
    /// </summary>
    /// <remarks>
    /// It matters here rather than being a detail: a singleton cannot hold a
    /// <see cref="DbContext"/>, which is why the repository takes a context
    /// factory. If this were ever changed to scoped, the reason for the factory
    /// would quietly stop applying and somebody would simplify it away.
    /// </remarks>
    [Fact]
    public void TheDatabaseContentStoreIsASingleton()
    {
        var services = NewServices();

        services.AddSw5ePersistence(Configuration());
        services.AddDatabaseContentStore();

        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(IContentRepository));

        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        descriptor.ImplementationType.ShouldBe(typeof(DbContentRepository));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IContentRepository>()
                .ShouldBeSameAs(provider.GetRequiredService<IContentRepository>());
    }

    private static ServiceProvider Build()
    {
        var services = NewServices();

        services.AddSw5ePersistence(Configuration());

        return services.BuildServiceProvider();
    }

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));

        return services;
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sw5e"] = ContentConnection,
                ["ConnectionStrings:Sw5eIdentity"] = IdentityConnection,
            })
            .Build();
}
