using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Infrastructure.Persistence;

/// <summary>
/// The one registration that puts SW5e on PostgreSQL.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared database connection, the content context and the
    /// database health check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shared data source is the point of this method.</b> Content and
    /// identity are separate contexts with separate schemas and separate
    /// migration histories, but they are one database and must be one
    /// connection pool. Registering <see cref="NpgsqlDataSource"/> as a
    /// singleton and having every context resolve it means there is exactly one
    /// place a credential is read, one pool to size, and no way for two
    /// features to end up pointed at two different servers. A second context
    /// added later resolves the same instance and needs no configuration of its
    /// own beyond its migrations history table.
    /// </para>
    /// <para>
    /// It is safe to call more than once; everything it adds is registered with
    /// try-add semantics, so two features that each depend on persistence can
    /// both ask for it.
    /// </para>
    /// </remarks>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">
    /// Configuration holding <c>ConnectionStrings:Sw5e</c> and, optionally, the
    /// <c>Sw5e:Database</c> section.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The connection string is missing. Thrown at registration rather than on
    /// first use, so a misconfigured deployment fails while it is starting
    /// instead of on a user's request.
    /// </exception>
    public static IServiceCollection AddSw5ePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(Sw5eDatabaseOptions.ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"No connection string named '{Sw5eDatabaseOptions.ConnectionStringName}' is configured. " +
                $"Set ConnectionStrings__{Sw5eDatabaseOptions.ConnectionStringName} for the deployment.");
        }

        // Calling this twice would register a second health check under the same
        // name, which throws at first probe rather than at startup. Content and
        // identity both depend on persistence, so both will ask for it.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)))
        {
            return services;
        }

        services.AddOptions<Sw5eDatabaseOptions>()
                .Bind(configuration.GetSection(Sw5eDatabaseOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.TryAddSingleton(provider =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);

            // Npgsql logs command text at debug level, and command text here
            // carries content names and search phrases rather than anything
            // sensitive. Parameter values are deliberately left out: the
            // identity context shares this data source, and its parameters are
            // email addresses and token hashes.
            builder.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());

            return builder.Build();
        });

        // A pooled factory rather than a plain scoped context, because the main
        // consumer of the content context is a singleton: IContentRepository is
        // documented as safe for concurrent use and registered as a singleton,
        // and a singleton holding a DbContext is a race condition with a long
        // fuse. The factory hands out a fresh context per operation and returns
        // it to a pool afterwards, so the per-operation cost is a reset rather
        // than a model rebuild.
        services.AddPooledDbContextFactory<Sw5eContentDbContext>(
            (provider, options) => ConfigureContext(provider, configuration, options));

        // Also available as an ordinary scoped context, which is what the
        // migrator and the importer take: they are one-shot and single-threaded
        // and have no reason to manage a context themselves.
        services.TryAddScoped(provider =>
            provider.GetRequiredService<IDbContextFactory<Sw5eContentDbContext>>().CreateDbContext());

        services.AddHealthChecks()
                .AddCheck<Sw5eDatabaseHealthCheck>(
                    Sw5eDatabaseHealthCheck.Name,
                    failureStatus: HealthStatus.Unhealthy,
                    tags: [Sw5eDatabaseHealthCheck.ReadyTag]);

        return services;
    }

    /// <summary>
    /// Registers <see cref="DbContentRepository"/> as the content store.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddSw5ePersistence"/> so that having a database
    /// and serving content from it stay independent decisions. Identity needs
    /// the database whether or not the catalogue lives there, and a deployment
    /// mid-migration may want both registered and only one of them in use.
    /// </remarks>
    public static IServiceCollection AddDatabaseContentStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IContentRepository, DbContentRepository>();

        return services;
    }

    /// <summary>
    /// Registers the content importer, which needs a scoped context and is only
    /// wanted by the migrator.
    /// </summary>
    public static IServiceCollection AddSw5eContentImporter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ContentImporter>();

        return services;
    }

    private static void ConfigureContext(
        IServiceProvider provider,
        IConfiguration configuration,
        DbContextOptionsBuilder builder)
    {
        // Read directly rather than through IOptions: this runs while the
        // singleton graph is being built, and the options monitor is not
        // something to depend on from inside a factory construction.
        var settings = configuration.GetSection(Sw5eDatabaseOptions.SectionName)
                                    .Get<Sw5eDatabaseOptions>() ?? new Sw5eDatabaseOptions();

        builder.UseNpgsql(
            provider.GetRequiredService<NpgsqlDataSource>(),
            npgsql =>
            {
                // Kept inside the content schema so a second context's history
                // table cannot collide with it. Two contexts sharing one
                // history table each treat the other's rows as migrations of
                // their own that have already run, and the first Migrate after
                // that tries to create tables that exist.
                npgsql.MigrationsHistoryTable(
                    Sw5eContentDbContext.MigrationsHistoryTableName,
                    Sw5eContentDbContext.SchemaName);

                npgsql.CommandTimeout(settings.CommandTimeoutSeconds);

                if (settings.MaxRetryCount > 0)
                {
                    npgsql.EnableRetryOnFailure(
                        settings.MaxRetryCount,
                        TimeSpan.FromSeconds(settings.MaxRetryDelaySeconds),
                        errorCodesToAdd: null);
                }
            });

        builder.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());
    }
}
