using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Persistence;
using Sw5e.Infrastructure.Persistence.Content;
using Testcontainers.PostgreSql;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The PostgreSQL server every test in this project runs against.
/// </summary>
/// <remarks>
/// <para>
/// One container for the whole assembly, and one database per test class inside
/// it. Starting a container costs seconds; creating a database on a running one
/// costs milliseconds, so per-class isolation is affordable — and it is worth
/// having, because these tests import, mutate and delete content and would
/// otherwise be reading each other's rows.
/// </para>
/// <para>
/// The image is pinned to the same major version the QA stack runs. Testing
/// against a different one would leave exactly the behaviour these tests exist
/// to pin down — collation, jsonb normalisation, index method support —
/// unverified for the version that actually serves the site.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Connection string for the server's default database.</summary>
    public string AdminConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The PostgreSQL container was not started.");

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            // Every test in this project carries [DockerFact], so they will all
            // report as skipped. Throwing here instead would report them as
            // failures, which is a different and much less useful signal.
            return;
        }

        // The image is named in the constructor rather than through WithImage:
        // the parameterless builder is obsolete, and passing the image here is
        // what makes the pin unambiguous.
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("sw5e")
            .WithUsername("sw5e")
            .WithPassword("sw5e-test-only")
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates an empty database on the shared server and returns a handle to
    /// it, with nothing applied.
    /// </summary>
    /// <param name="name">
    /// Database name. Must be a plain lowercase identifier: it is interpolated
    /// into a CREATE DATABASE statement, which takes no parameters.
    /// </param>
    /// <param name="contentRoot">
    /// Directory the services should treat as the content corpus. Defaults to
    /// the committed fixture; the migrator tests override it to exercise a path
    /// that holds nothing.
    /// </param>
    public async Task<ContentDatabase> CreateDatabaseAsync(string name, string? contentRoot = null)
    {
        if (!name.All(character => char.IsAsciiLetterLower(character) || character == '_'))
        {
            throw new ArgumentException(
                "Database names in these tests must be lowercase letters and underscores.",
                nameof(name));
        }

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        // Dropped first so a rerun after a crashed test session starts clean
        // rather than inheriting whatever the last one left behind.
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE {name}";
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name };

        return new ContentDatabase(builder.ConnectionString, contentRoot ?? ContentFixture.Path);
    }
}

/// <summary>
/// One test class's own database, with the services that talk to it.
/// </summary>
public sealed class ContentDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    internal ContentDatabase(string connectionString, string contentRoot)
    {
        ConnectionString = connectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sw5e"] = connectionString,
                ["Content:RootPath"] = contentRoot,

                // Retries are switched off for the tests. The behaviour they
                // add is a delay before a failure, which turns a broken query
                // into a slow broken query and makes a red run take twenty
                // seconds longer to say the same thing.
                ["Sw5e:Database:MaxRetryCount"] = "0",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSw5ePersistence(configuration);
        services.AddSw5eContentImporter();
        services.AddDatabaseContentStore();

        _services = services.BuildServiceProvider();
    }

    public string ConnectionString { get; }

    public IServiceProvider Services => _services;

    /// <summary>The database-backed content store, as the API resolves it.</summary>
    public IContentRepository Repository => _services.GetRequiredService<IContentRepository>();

    /// <summary>A fresh context. The caller disposes it.</summary>
    public Sw5eContentDbContext CreateContext() =>
        _services.GetRequiredService<IDbContextFactory<Sw5eContentDbContext>>().CreateDbContext();

    /// <summary>Applies every migration.</summary>
    public async Task MigrateAsync()
    {
        await using var database = CreateContext();
        await database.Database.MigrateAsync();
    }

    /// <summary>Imports content from <paramref name="rootPath"/>, defaulting to the fixture.</summary>
    public async Task<ContentImportResult> ImportAsync(string? rootPath = null)
    {
        using var scope = _services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ContentImporter>();

        return await importer.ImportAsync(rootPath ?? ContentFixture.Path);
    }

    /// <summary>Applies migrations and imports the fixture, which is the usual starting point.</summary>
    public async Task<ContentImportResult> MigrateAndImportAsync()
    {
        await MigrateAsync();
        return await ImportAsync();
    }

    /// <summary>
    /// A second handle on the same database, configured to read its content
    /// from somewhere else.
    /// </summary>
    /// <remarks>
    /// Used to point the migrator at a content directory that holds nothing
    /// while leaving the catalogue it already imported in place. Going back
    /// through the fixture would drop and recreate the database, which is the
    /// opposite of what the test is trying to observe.
    /// </remarks>
    public ContentDatabase WithContentRoot(string contentRoot) =>
        new(ConnectionString, contentRoot);

    /// <summary>An open connection, for asserting on the schema itself.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    /// <summary>Runs a scalar query. Used to interrogate the catalogue directly.</summary>
    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? default : (T)result;
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}

/// <summary>The committed content fixture these tests import.</summary>
public static class ContentFixture
{
    /// <summary>Absolute path to the fixture, copied beside the test assembly.</summary>
    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestContent");

    /// <summary>
    /// How many documents the fixture holds per type, once the deliberately
    /// invalid one has been skipped.
    /// </summary>
    /// <remarks>
    /// Written down here so that every test asserting a count asserts against
    /// the same declared expectation. A test that counted the files on disk
    /// instead would pass no matter what the importer did with them.
    /// </remarks>
    public static IReadOnlyDictionary<string, int> ExpectedCounts { get; } =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["source"] = 2,
            ["species"] = 4,
            ["background"] = 1,
            ["archetype"] = 1,
            ["feature"] = 2,
            ["feat"] = 3,
            ["power"] = 3,
            ["equipment"] = 2,
            ["monster"] = 1,
        };

    /// <summary>Total valid documents in the fixture.</summary>
    public static int ExpectedTotal { get; } = ExpectedCounts.Values.Sum();
}

/// <summary>
/// Shares one container across every test class in the assembly.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
