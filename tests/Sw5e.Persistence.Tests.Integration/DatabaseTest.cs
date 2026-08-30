namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Base for a test class that needs a database of its own.
/// </summary>
/// <remarks>
/// Per-class rather than per-test: creating a database and applying the
/// migrations costs about a second, and the tests within a class either do not
/// mutate the catalogue or say in their name that they do. Sharing across
/// classes was tried and is worse — the importer tests delete content, and
/// everything else then reads whatever they happened to leave.
/// </remarks>
[Collection(PostgresCollection.Name)]
public abstract class DatabaseTest(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// The shared PostgreSQL server. Exposed here rather than captured again by
    /// each derived class, which would give two fields holding the same value.
    /// </summary>
    protected PostgresFixture Fixture { get; } = fixture;

    /// <summary>
    /// The database this class owns. Null only when Docker is unreachable, in
    /// which case every test in the class is skipped before it can be touched.
    /// </summary>
    protected ContentDatabase Database { get; private set; } = null!;

    /// <summary>Lowercase identifier for this class's database.</summary>
    protected abstract string DatabaseName { get; }

    /// <summary>Whether to apply migrations before the first test.</summary>
    protected virtual bool Migrate => true;

    /// <summary>Whether to import the content fixture before the first test.</summary>
    protected virtual bool ImportContent => true;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        Database = await Fixture.CreateDatabaseAsync(DatabaseName);

        if (Migrate)
        {
            await Database.MigrateAsync();
        }

        if (ImportContent)
        {
            await Database.ImportAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (Database is not null)
        {
            await Database.DisposeAsync();
        }
    }
}
