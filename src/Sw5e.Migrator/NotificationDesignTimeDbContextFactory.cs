using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sw5e.Infrastructure.Persistence.Notifications;

namespace Sw5e.Migrator;

/// <summary>
/// Builds a notification context for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// The same placeholder-connection reasoning as
/// <see cref="DesignTimeDbContextFactory"/>: scaffolding a migration reads the
/// model and writes C#, and never opens a connection, so the string below is
/// deliberately unusable. Applying migrations is the migrator's job, run
/// against a database the deployment names.
/// </remarks>
public sealed class NotificationDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<Sw5eNotificationDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=design-time.invalid;Database=sw5e;Username=sw5e;Password=sw5e";

    public Sw5eNotificationDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Sw5eNotifications")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Sw5e")
            ?? PlaceholderConnectionString;

        var builder = new DbContextOptionsBuilder<Sw5eNotificationDbContext>();

        builder.UseNpgsql(connectionString, npgsql =>
        {
            // Must match the runtime configuration, or a migration would be
            // recorded in one history table and looked for in another.
            npgsql.MigrationsHistoryTable(
                Sw5eNotificationDbContext.MigrationsHistoryTableName,
                Sw5eNotificationDbContext.SchemaName);

            // Migrations live beside the model in Sw5e.Infrastructure, as the
            // content and moderation ones do, so the assembly that defines a
            // schema is the one that carries its history.
            npgsql.MigrationsAssembly(typeof(Sw5eNotificationDbContext).Assembly.GetName().Name);
        });

        return new Sw5eNotificationDbContext(builder.Options);
    }
}
