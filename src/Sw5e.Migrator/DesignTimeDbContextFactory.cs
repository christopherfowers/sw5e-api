using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Migrator;

/// <summary>
/// Builds a content context for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the tooling would have to build the application's own service
/// graph to find a context, and that graph refuses to be built without a real
/// connection string — so scaffolding a migration would require a configured
/// database on the developer's machine. Adding a migration is a compile-time
/// activity: it reads the model and writes C#, and it never opens a connection.
/// </para>
/// <para>
/// The connection string below is therefore a placeholder and is deliberately
/// unusable. If a command that genuinely needs a database is run — <c>dotnet ef
/// database update</c>, say — it fails to connect rather than quietly reaching a
/// real one, which is the right outcome: applying migrations is the migrator's
/// job, run deliberately against a database named by the deployment. A real
/// connection string can still be supplied for a one-off through the
/// <c>ConnectionStrings__Sw5e</c> environment variable.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<Sw5eContentDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=design-time.invalid;Database=sw5e;Username=sw5e;Password=sw5e";

    public Sw5eContentDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Sw5e") ?? PlaceholderConnectionString;

        var builder = new DbContextOptionsBuilder<Sw5eContentDbContext>();

        builder.UseNpgsql(connectionString, npgsql =>
        {
            // Must match the runtime configuration. A migration scaffolded
            // against a different history table would be recorded in one place
            // and looked for in another, and every deploy would try to apply it
            // again.
            npgsql.MigrationsHistoryTable(
                Sw5eContentDbContext.MigrationsHistoryTableName,
                Sw5eContentDbContext.SchemaName);

            // Migrations live beside the model in Sw5e.Infrastructure rather
            // than in this project, so the assembly that defines the schema is
            // also the one that carries its history.
            npgsql.MigrationsAssembly(typeof(Sw5eContentDbContext).Assembly.GetName().Name);
        });

        return new Sw5eContentDbContext(builder.Options);
    }
}
