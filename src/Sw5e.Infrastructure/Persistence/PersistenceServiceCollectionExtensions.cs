using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Content;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Infrastructure.Persistence;

/// <summary>
/// The one registration that puts SW5e on PostgreSQL.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the content database connection, the content context and the
    /// database health check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This registers the connection for content, and only for content.</b>
    /// The data source is keyed with <see cref="ContentDataSourceKey"/> rather
    /// than registered as a bare <see cref="NpgsqlDataSource"/> singleton, and
    /// that is deliberate. Identity resolves its own connection string —
    /// <c>Identity:ConnectionString</c>, then <c>ConnectionStrings:Sw5eIdentity</c>,
    /// then <c>ConnectionStrings:Sw5e</c> — precisely so a deployment can give
    /// account data a least-privileged role, or a database of its own, without
    /// touching anything here. An unkeyed singleton would sit in the container
    /// waiting for someone to resolve it "to share the pool", and would then
    /// route account data down the content connection with nothing to say it
    /// had happened. Being unable to reach it by accident is worth more than
    /// the pool that sharing would have saved.
    /// </para>
    /// <para>
    /// A second context that genuinely wants this connection can still ask for
    /// it by key, which makes sharing a decision someone wrote down rather than
    /// something that happened.
    /// </para>
    /// <para>
    /// It is safe to call more than once: a second call returns immediately.
    /// Without the guard, a second call would register a second health check
    /// under the same name, which throws at the first probe rather than at
    /// startup.
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
    /// <summary>
    /// Service key the content data source is registered under.
    /// </summary>
    /// <remarks>
    /// Public so a second context can opt into this connection explicitly. It
    /// is not the connection string's name: the connection string is shared
    /// configuration, this key names one pool built from it.
    /// </remarks>
    public const string ContentDataSourceKey = "Sw5eContent";

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
        // name, which throws at the first probe rather than at startup.
        if (services.Any(descriptor =>
                descriptor.IsKeyedService &&
                descriptor.ServiceType == typeof(NpgsqlDataSource) &&
                Equals(descriptor.ServiceKey, ContentDataSourceKey)))
        {
            return services;
        }

        services.AddOptions<Sw5eDatabaseOptions>()
                .Bind(configuration.GetSection(Sw5eDatabaseOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.TryAddKeyedSingleton(ContentDataSourceKey, (provider, _) =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);

            // Npgsql logs command text at debug level. Parameter logging is
            // deliberately left off: content names and search phrases are not
            // sensitive, but the switch is per data source rather than per
            // query, and a habit of enabling it here is a habit that reaches
            // somewhere it should not.
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
    /// Separate from <see cref="AddSw5ePersistence"/> so that having a content
    /// database and serving content from it stay independent decisions: the
    /// migrator wants the first without the second, and a deployment part-way
    /// through a migration may want both registered and only one in use.
    /// </remarks>
    public static IServiceCollection AddDatabaseContentStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IContentRepository, DbContentRepository>();

        return services;
    }

    /// <summary>
    /// Registers the authoring store and the schema validator that guards it.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="schemaRootPath">
    /// Directory holding one subdirectory per content type, each with its
    /// versioned JSON Schema documents.
    /// </param>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AddDatabaseContentStore"/> because reading the
    /// catalogue out of PostgreSQL and letting people write to it are different
    /// decisions with different blast radii. A deployment can serve content from
    /// the database while authoring is still off.
    /// </para>
    /// <para>
    /// Never registered for the file-backed store. That store reads a volume
    /// mounted read-only and builds its index once at start-up, so a write
    /// against it could neither land nor be seen. The endpoints resolve this
    /// service optionally and answer 503 when it is missing, which is an honest
    /// "not here" rather than a 404 that would say the feature does not exist.
    /// </para>
    /// <para>
    /// The validator is a singleton: it compiles each schema once and caches it,
    /// and building a fresh one per request would recompile all 31 on every
    /// write.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddContentAuthoring(
        this IServiceCollection services,
        string schemaRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaRootPath);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IContentSchemaValidator>(
            _ => new ContentSchemaValidator(schemaRootPath));

        services.AddScoped<IContentAuthoringStore, DbContentAuthoringStore>();

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
            provider.GetRequiredKeyedService<NpgsqlDataSource>(ContentDataSourceKey),
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
