using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sw5e.Infrastructure.Persistence.Moderation;

/// <summary>
/// Registers the store the flagging feature writes to.
/// </summary>
public static class ModerationServiceCollectionExtensions
{
    /// <summary>
    /// Works out which database the moderation schema lives in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four sources, in this order:
    /// </para>
    /// <list type="number">
    /// <item><description><c>Moderation:ConnectionString</c></description></item>
    /// <item><description><c>ConnectionStrings:Sw5eModeration</c></description></item>
    /// <item><description><c>ConnectionStrings:Sw5e</c> — the platform database</description></item>
    /// <item><description><c>ConnectionStrings:Sw5eIdentity</c></description></item>
    /// </list>
    /// <para>
    /// The last entry is the one worth explaining. A deployment that serves
    /// content from files rather than from PostgreSQL has no reason to set
    /// <c>ConnectionStrings:Sw5e</c> at all, and one of them is running: the
    /// site's own container smoke test. Refusing to start there would mean the
    /// arrival of flagging broke a configuration that had nothing to do with
    /// it, so the identity connection is accepted as a last resort — accounts
    /// exist in every deployment, and moderation data has far more in common
    /// with them than with the content catalogue anyway.
    /// </para>
    /// <para>
    /// The order is a public method rather than a private detail because the
    /// migrator has to resolve the same connection the API will. Two
    /// implementations of this precedence is how a deployment ends up with the
    /// schema applied to one database and the endpoint writing to another,
    /// which fails at the first report rather than at deploy time.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Nothing is configured.</exception>
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString =
            configuration["Moderation:ConnectionString"] ??
            configuration.GetConnectionString("Sw5eModeration") ??
            configuration.GetConnectionString("Sw5e") ??
            configuration.GetConnectionString("Sw5eIdentity");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No database is configured for the moderation schema. Set one of " +
                "Moderation__ConnectionString, ConnectionStrings__Sw5eModeration, " +
                "ConnectionStrings__Sw5e or ConnectionStrings__Sw5eIdentity.");
        }

        return connectionString;
    }

    /// <summary>
    /// Registers the moderation context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain scoped context rather than the pooled factory the content store
    /// uses. The content repository is a singleton serving every anonymous read
    /// on the site and needs a context per operation; this one is used by
    /// request-scoped endpoint handlers only, at a volume measured in reports
    /// per day.
    /// </para>
    /// <para>
    /// <b>No health check, deliberately.</b> Adding one would make
    /// <c>/health/ready</c> report the whole deployment unhealthy when the
    /// moderation database is unreachable, and that is the wrong trade by a
    /// wide margin: the reference is what the site is for, it is served from an
    /// entirely different store, and taking it out of the load balancer because
    /// nobody can file a typo report would turn a small outage into a large
    /// one. A broken moderation store shows up as a failing flag endpoint,
    /// which is where it belongs.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSw5eModeration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = ResolveConnectionString(configuration);

        services.AddDbContext<Sw5eModerationDbContext>(builder => builder
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable(
                    Sw5eModerationDbContext.MigrationsHistoryTableName,
                    Sw5eModerationDbContext.SchemaName)));

        return services;
    }

    /// <summary>
    /// Brings the moderation schema up to what this build expects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by the migrator, which is the only thing that should call it. The
    /// API does not migrate at startup, for the reasons the migrator's own
    /// entry point sets out: every replica runs startup, so N replicas race one
    /// another to apply the same migration, and a web process that can change
    /// the schema holds rights at runtime that it has no business holding.
    /// </para>
    /// <para>
    /// It lives here rather than in the migrator so the test host can play the
    /// migrator's part without the test project taking a dependency on an
    /// executable — and so there is one implementation of "bring this schema
    /// up to date" rather than one the deployment runs and one the tests
    /// approximate.
    /// </para>
    /// </remarks>
    public static async Task MigrateModerationAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eModerationDbContext>();

        var pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

        if (pending.Length == 0)
        {
            logger.LogInformation("The moderation schema is already up to date.");
            return;
        }

        logger.LogInformation(
            "Applying {Count} moderation migration(s): {Migrations}",
            pending.Length,
            string.Join(", ", pending));

        await database.Database.MigrateAsync(cancellationToken);

        logger.LogInformation("Moderation migrations applied.");
    }
}
