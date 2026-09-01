using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sw5e.Infrastructure.Persistence.Moderation;

namespace Sw5e.Migrator;

/// <summary>
/// Builds a moderation context for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// The same placeholder-connection reasoning as
/// <see cref="DesignTimeDbContextFactory"/>: scaffolding a migration reads the
/// model and writes C#, and never opens a connection, so the string below is
/// deliberately unusable. Applying migrations is the migrator's job, run
/// against a database the deployment names.
/// </remarks>
public sealed class ModerationDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<Sw5eModerationDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=design-time.invalid;Database=sw5e;Username=sw5e;Password=sw5e";

    public Sw5eModerationDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Sw5eModeration")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Sw5e")
            ?? PlaceholderConnectionString;

        var builder = new DbContextOptionsBuilder<Sw5eModerationDbContext>();

        builder.UseNpgsql(connectionString, npgsql =>
        {
            // Must match the runtime configuration, or a migration would be
            // recorded in one history table and looked for in another.
            npgsql.MigrationsHistoryTable(
                Sw5eModerationDbContext.MigrationsHistoryTableName,
                Sw5eModerationDbContext.SchemaName);

            // Migrations live beside the model in Sw5e.Infrastructure, as the
            // content ones do, so the assembly that defines a schema is the one
            // that carries its history.
            npgsql.MigrationsAssembly(typeof(Sw5eModerationDbContext).Assembly.GetName().Name);
        });

        return new Sw5eModerationDbContext(builder.Options);
    }
}
