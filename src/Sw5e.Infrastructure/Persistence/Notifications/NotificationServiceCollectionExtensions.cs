using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sw5e.Domain.Notifications;

namespace Sw5e.Infrastructure.Persistence.Notifications;

/// <summary>
/// Registering the notification schema and the service over it.
/// </summary>
/// <remarks>
/// Follows the moderation registration rather than inventing a second shape:
/// its own connection string setting, falling back to the shared one, its own
/// migrations history table inside its own schema.
/// </remarks>
public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Where this schema's database comes from.
    /// </summary>
    /// <remarks>
    /// Its own setting first, so a deployment that wants it on a separate
    /// server can say so, then the shared ones. Most deployments set only
    /// <c>ConnectionStrings__Sw5e</c> and never think about this.
    /// </remarks>
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString =
            configuration["Notifications:ConnectionString"] ??
            configuration.GetConnectionString("Sw5eNotifications") ??
            configuration.GetConnectionString("Sw5e") ??
            configuration.GetConnectionString("Sw5eIdentity");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No database is configured for the notifications schema. Set one of " +
                "Notifications__ConnectionString, ConnectionStrings__Sw5eNotifications, " +
                "ConnectionStrings__Sw5e or ConnectionStrings__Sw5eIdentity.");
        }

        return connectionString;
    }

    public static IServiceCollection AddSw5eNotifications(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = ResolveConnectionString(configuration);

        /*
          A factory rather than a scoped context.

          Notifications are written at the end of a publish, from code that
          already holds its own context and its own transaction over the
          content schema. Sharing a scoped context across the two would tie a
          notification's fate to a transaction it has no business being inside:
          a notification that cannot be written must not roll back the publish
          it is about.
        */
        services.AddDbContextFactory<Sw5eNotificationDbContext>(builder => builder
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable(
                    Sw5eNotificationDbContext.MigrationsHistoryTableName,
                    Sw5eNotificationDbContext.SchemaName)));

        services.AddScoped<INotificationService, NotificationService>();

        return services;
    }

    /// <summary>
    /// Brings the notification schema up to what this build expects.
    /// </summary>
    /// <remarks>
    /// Called by the migrator and by the test host, and by nothing else. The
    /// API does not migrate at startup: every replica runs startup, so N
    /// replicas race to apply the same migration, and a web process that can
    /// change the schema holds rights at runtime it has no business holding.
    /// </remarks>
    public static async Task MigrateNotificationsAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = services.CreateAsyncScope();

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<Sw5eNotificationDbContext>>();

        await using var database = await factory.CreateDbContextAsync(cancellationToken);

        var pending = await database.Database.GetPendingMigrationsAsync(cancellationToken);
        var names = pending.ToArray();

        if (names.Length == 0)
        {
            logger.LogInformation("Notifications: schema is already up to date.");
            return;
        }

        logger.LogInformation(
            "Notifications: applying {Count} migration(s): {Migrations}.",
            names.Length,
            string.Join(", ", names));

        await database.Database.MigrateAsync(cancellationToken);
    }
}
