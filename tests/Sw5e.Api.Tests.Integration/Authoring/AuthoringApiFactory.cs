using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// An API host with real accounts, a real content database and the real JSON
/// Schemas.
/// </summary>
/// <remarks>
/// <para>
/// Authoring is the first thing on this platform that needs all three at once.
/// The account factory already provides identity and moderation against a real
/// PostgreSQL instance; this adds the content schema and switches the store
/// over, because the authoring store is registered only alongside the database
/// content store and a file-backed host has nothing to write to.
/// </para>
/// <para>
/// The schemas are the ones from the content repository, copied beside the test
/// assembly by the project file. Substituting a permissive test schema would
/// make the "a write that violates the schema is refused" test prove nothing
/// about the corpus it is supposed to protect.
/// </para>
/// </remarks>
public class AuthoringApiFactory : AccountApiFactory
{
    private readonly string _connectionString;

    // A written-out constructor rather than a primary one. A primary
    // constructor parameter that is both passed to the base and used in the
    // body is captured into a second field holding the same object the base
    // already holds, which the compiler refuses (CS9107). Only the connection
    // string is needed here, so only that is kept.
    public AuthoringApiFactory(PostgresFixture postgres)
        : base(postgres) =>
        _connectionString = postgres.ConnectionString;

    /// <summary>The real schema documents, copied beside the test assembly.</summary>
    public static string SchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "TestSchemas");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // The whole point of this factory. Everything else here follows from it.
        builder.UseSetting("Content:Store", "database");
        builder.UseSetting("ConnectionStrings:Sw5e", _connectionString);
        builder.UseSetting("Content:SchemaPath", SchemaPath);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Runs the moderation migration, among other things.
        var host = base.CreateHost(builder);

        // Content schema, applied the way the deploy-time migrator applies it:
        // through EF's own Migrate, so the tests exercise the migrations that
        // will actually run rather than a model the test built for itself. That
        // is what makes the append-only trigger part of what is under test —
        // it exists only in a migration.
        using (var scope = host.Services.CreateScope())
        {
            var content = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();
            content.Database.Migrate();
        }

        return host;
    }

    /// <summary>
    /// Removes every draft, revision and content item, so one test cannot see
    /// another's writes.
    /// </summary>
    /// <remarks>
    /// Revisions go by <c>TRUNCATE</c> rather than <c>DELETE</c>. The
    /// append-only guard is a row-level BEFORE DELETE trigger, and PostgreSQL
    /// does not fire those for a truncate — so the fixture can clear its own
    /// data without disabling the guard, and without needing to know how. A
    /// <c>DELETE</c> here would be refused, which is exactly what
    /// <see cref="ContentRevisionImmutabilityTests"/> asserts on directly.
    /// </remarks>
    public async Task ResetContentAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();

        await database.Database.ExecuteSqlRawAsync("TRUNCATE content.content_revision;");
        await database.Database.ExecuteSqlRawAsync("DELETE FROM content.content_draft;");
        await database.Database.ExecuteSqlRawAsync("DELETE FROM content.content_reference;");
        await database.Database.ExecuteSqlRawAsync("DELETE FROM content.content_item;");
    }
}
