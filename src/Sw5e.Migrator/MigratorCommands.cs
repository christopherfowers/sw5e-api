using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sw5e.Infrastructure.Persistence.Content;
using Sw5e.Infrastructure.Persistence.Moderation;

namespace Sw5e.Migrator;

/// <summary>
/// What the deploy-time database job actually does.
/// </summary>
/// <remarks>
/// Separated from the entry point so the integration tests drive this rather
/// than a reimplementation of it. A test that reproduces the migrator's steps
/// in its own code proves those steps work; it proves nothing about the
/// executable the deployment runs, which is the thing that can be broken.
/// </remarks>
public static class MigratorCommands
{
    /// <summary>Everything asked for succeeded.</summary>
    public const int Success = 0;

    /// <summary>A command failed. The reason is in the log.</summary>
    public const int Failed = 1;

    /// <summary>The command name was not recognised.</summary>
    public const int UnknownCommand = 2;

    /// <summary>Runs one command against an already-configured service provider.</summary>
    /// <param name="services">Root provider, with persistence and the importer registered.</param>
    /// <param name="command">One of <c>migrate</c>, <c>import</c> or <c>all</c>.</param>
    /// <param name="logger">Where progress and failures are reported.</param>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string command,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            switch (command)
            {
                case "migrate":
                    await MigrateAsync(services, logger, cancellationToken);

                    // Two schemas, two migration histories, one command. They
                    // are separate contexts so that each can be authored and
                    // reviewed on its own, and they are applied together
                    // because a deployment that migrated one and not the other
                    // is a deployment where half the site works — and because
                    // the alternative is a second job somebody has to remember
                    // to add to the pipeline.
                    await MigrateModerationAsync(services, logger, cancellationToken);
                    break;

                case "import":
                    await ImportAsync(services, logger, cancellationToken);
                    break;

                case "all":
                    // Order is not negotiable: the importer writes to tables the
                    // migrations create.
                    await MigrateAsync(services, logger, cancellationToken);
                    await MigrateModerationAsync(services, logger, cancellationToken);
                    await ImportAsync(services, logger, cancellationToken);
                    break;

                default:
                    logger.LogError(
                        "Unknown command '{Command}'. Expected one of: migrate, import, all.",
                        command);

                    return UnknownCommand;
            }

            return Success;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Cancelled before completing. No partial work was committed.");
            return Failed;
        }
        catch (Exception exception)
        {
            // Caught rather than allowed to escape, so the failure reaches the
            // deploy log in the same shape as everything else and the process
            // still returns a code the orchestrator can branch on.
            logger.LogError(exception, "The migrator failed.");
            return Failed;
        }
    }

    /// <summary>Brings the content schema up to what this build expects.</summary>
    public static async Task MigrateAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();

        var pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

        if (pending.Length == 0)
        {
            logger.LogInformation("The content schema is already up to date.");
            return;
        }

        logger.LogInformation(
            "Applying {Count} migration(s): {Migrations}",
            pending.Length,
            string.Join(", ", pending));

        await database.Database.MigrateAsync(cancellationToken);

        logger.LogInformation("Migrations applied.");
    }

    /// <summary>
    /// Brings the moderation schema up to what this build expects.
    /// </summary>
    /// <remarks>
    /// A thin forward to the implementation that lives beside the model, so
    /// that the test host can play the migrator's part without depending on
    /// this executable — and so there is exactly one implementation of "bring
    /// the moderation schema up to date" rather than one the deployment runs
    /// and one the tests approximate.
    /// </remarks>
    public static Task MigrateModerationAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default) =>
        ModerationServiceCollectionExtensions.MigrateModerationAsync(
            services, logger, cancellationToken);

    /// <summary>Loads the canonical content into the database.</summary>
    /// <exception cref="InvalidOperationException">
    /// No content path is configured, or the configured path yielded nothing.
    /// </exception>
    public static async Task<ContentImportResult> ImportAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        // The same setting name the API uses for its file-backed store, so one
        // environment variable points both at the same corpus and the compose
        // stack does not carry two spellings of one path.
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var rootPath = configuration["Content:RootPath"];

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException(
                "Content:RootPath is not configured, so there is nothing to import. Set " +
                "Content__RootPath to the directory holding the content type folders.");
        }

        var importer = scope.ServiceProvider.GetRequiredService<ContentImporter>();
        var result = await importer.ImportAsync(rootPath, cancellationToken);

        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Import: {Warning}", warning);
        }

        // An import that wrote nothing is not by itself a failure — redeploying
        // unchanged content is exactly that, and is the normal case. An import
        // that found nothing to write is, because the only way to reach it is a
        // content directory that was missing or unreadable, and continuing would
        // publish an empty catalogue as though it were the real one. The
        // breakdown in the result is what makes the two distinguishable.
        if (result is { Inserted: 0, Updated: 0, Unchanged: 0 })
        {
            throw new InvalidOperationException(
                $"No content was found under '{rootPath}'. The catalogue was left untouched. " +
                "Check that the content volume is mounted and readable.");
        }

        return result;
    }
}
