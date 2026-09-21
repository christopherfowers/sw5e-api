using Testcontainers.PostgreSql;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// A real PostgreSQL instance for the account tests, started once and shared.
/// </summary>
/// <remarks>
/// <para>
/// Real, not in-memory, and not SQLite. The identity schema uses a JSON column
/// for passkey data, a non-default schema, and a unique index that the
/// registration flow depends on to settle a race; a substitute provider models
/// none of those faithfully, so a test suite running against one would be
/// asserting on behaviour the deployed system does not have.
/// </para>
/// <para>
/// This is also the only place the migration itself is exercised. Every test
/// below runs against a database built by applying the committed migration to
/// an empty instance, so a migration that does not produce a schema the
/// framework can use fails the suite rather than the first deployment.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        // Pinned. An unpinned tag makes the suite's meaning drift with whatever
        // the registry served that morning.
        new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("sw5e_identity_tests")
        .WithUsername("sw5e_tests")

        // A throwaway credential for a container that exists for the length of
        // one test run, listens on an ephemeral loopback port, and is destroyed
        // afterwards. It is not a secret and is not treated as one.
        .WithPassword("sw5e_tests")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// Binds every account test class to the one container, so the image is pulled
/// and started once rather than per class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AccountTestCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "accounts";
}
