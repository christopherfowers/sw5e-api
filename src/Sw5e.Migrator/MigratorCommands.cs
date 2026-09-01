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

    /// <summary>
    /// <c>export --check</c> found the database and the tree disagreeing.
    /// </summary>
    /// <remarks>
    /// Its own code, distinct from <see cref="Failed"/>, because the two mean
    /// opposite things to whatever is running the command: a failure is
    /// something to page about, and a disagreement is the normal state of a
    /// repository that has not been exported since somebody published. A
    /// scheduled job branches on this to decide whether there is anything worth
    /// opening a pull request for.
    /// </remarks>
    public const int Differs = 3;

    /// <summary>Runs one command against an already-configured service provider.</summary>
    /// <param name="services">Root provider, with persistence and the importer registered.</param>
    /// <param name="command">
    /// One of <c>migrate</c>, <c>import</c>, <c>export</c> or <c>all</c>.
    /// </param>
    /// <param name="logger">Where progress and failures are reported.</param>
    /// <param name="cancellationToken">Honoured between documents and types.</param>
    /// <param name="export">
    /// What <c>export</c> was asked for. Ignored by every other command.
    /// </param>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string command,
        ILogger logger,
        CancellationToken cancellationToken = default,
        ExportOptions? export = null)
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

                case "export":
                    return await ExportAsync(
                        services, export ?? ExportOptions.None, logger, cancellationToken);

                case "all":
                    // Deliberately without `export`. This is what a deploy
                    // runs, and a deploy runs it in a container that has no
                    // checkout to write to, no identity to attribute a change
                    // to, and nobody watching. Exporting is something a person
                    // or a scheduled job asks for, and asking for it is how it
                    // gets reviewed.
                    //
                    // Order is not negotiable: the importer writes to tables the
                    // migrations create.
                    await MigrateAsync(services, logger, cancellationToken);
                    await MigrateModerationAsync(services, logger, cancellationToken);
                    await ImportAsync(services, logger, cancellationToken);
                    break;

                default:
                    logger.LogError(
                        "Unknown command '{Command}'. Expected one of: migrate, import, export, all.",
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

    /// <summary>
    /// Writes the published catalogue back out as the content repository holds
    /// it, and reports what disagreed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This produces a working tree and stops. It does not commit and it does
    /// not push, and that is the boundary on purpose. Writing files needs a
    /// path; committing needs an identity to attribute the change to, and
    /// pushing needs a credential with write access to the content repository,
    /// held by a process that already holds the whole catalogue. Neither buys
    /// anything a scheduled job running <c>git commit</c> beside this one does
    /// not — the review still happens in a pull request either way — and the
    /// credential is a real thing to get wrong.
    /// </para>
    /// <para>
    /// So the operator's loop is: run this against a checkout, look at
    /// <c>git status</c>, and open a pull request. <c>--check</c> is the same
    /// run with nothing written, for finding out whether there is anything to
    /// open one for.
    /// </para>
    /// </remarks>
    public static async Task<int> ExportAsync(
        IServiceProvider services,
        ExportOptions options,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(options.Output))
        {
            logger.LogError(
                "export needs somewhere to write. Pass --output <path to the content/ directory " +
                "of a sw5e-database checkout>. There is deliberately no default: an export " +
                "writes files.");

            return Failed;
        }

        using var scope = services.CreateScope();
        var exporter = scope.ServiceProvider.GetService<ContentExporter>();

        if (exporter is null)
        {
            logger.LogError(
                "The exporter is not available, which means no schema directory was found. It " +
                "needs the schemas both to order each document's members and to refuse to write " +
                "one the content repository would reject. Set Content__SchemaPath.");

            return Failed;
        }

        ContentExportResult result;

        try
        {
            result = await exporter.ExportAsync(
                new ContentExportRequest(
                    options.Output,
                    options.ContentType,
                    options.Key,
                    options.Prune,
                    options.Check),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            // A bad --type or --key is the operator's typo, not a fault. Logged
            // as the message alone so it reads as an answer rather than as a
            // stack trace they have to look past.
            logger.LogError("{Message}", exception.Message);
            return Failed;
        }

        logger.LogInformation(
            "Export: {Examined} document(s) examined, {Unchanged} already matching, " +
            "{Added} added, {Changed} changed, {Removed} removed.",
            result.Examined, result.Unchanged, result.Added, result.Changed, result.Removed);

        foreach (var change in result.Changes.Take(ChangesLogged))
        {
            logger.LogInformation("  {Change}", change);
        }

        if (result.Changes.Count > ChangesLogged)
        {
            logger.LogInformation(
                "  ... and {Count} more.", result.Changes.Count - ChangesLogged);
        }

        if (result.InAgreement)
        {
            logger.LogInformation(
                "The catalogue and the tree at {Root} agree. Nothing to commit.", options.Output);

            return Success;
        }

        if (options.Check)
        {
            logger.LogWarning(
                "The catalogue and the tree at {Root} disagree about {Count} document(s). " +
                "Nothing was written; run without --check to produce the tree.",
                options.Output, result.Changes.Count);

            return Differs;
        }

        logger.LogInformation(
            "Wrote {Count} change(s) into {Root}. Review them with git and open a pull request; " +
            "this command does not commit.",
            result.Changes.Count, options.Output);

        return Success;
    }

    /// <summary>
    /// How many differing documents are named in the log before the rest are
    /// summarised.
    /// </summary>
    /// <remarks>
    /// A first export against a repository that has drifted for months could
    /// name thousands, and a deploy log that has to be scrolled past is a
    /// deploy log nobody reads. The tree itself is the complete answer.
    /// </remarks>
    private const int ChangesLogged = 50;
}

/// <summary>What the <c>export</c> command was asked for.</summary>
/// <param name="Output">
/// The <c>content/</c> directory to write. No default: an export writes files.
/// </param>
/// <param name="ContentType">
/// Restrict the export to one type, for a targeted diff. Null exports all of
/// them.
/// </param>
/// <param name="Key">
/// Restrict the export to one document, which also switches pruning off: a
/// single-document export has no opinion about any other file.
/// </param>
/// <param name="Prune">Delete files the catalogue no longer publishes.</param>
/// <param name="Check">Report the differences and write nothing.</param>
public sealed record ExportOptions(
    string? Output,
    string? ContentType = null,
    string? Key = null,
    bool Prune = true,
    bool Check = false)
{
    /// <summary>Nothing asked for, which <c>export</c> refuses.</summary>
    public static ExportOptions None { get; } = new(Output: null);
}
