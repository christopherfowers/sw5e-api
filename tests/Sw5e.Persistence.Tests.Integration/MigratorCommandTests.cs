using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sw5e.Migrator;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The deploy-time job, driven through the same entry point the deployment
/// runs.
/// </summary>
/// <remarks>
/// These call <see cref="MigratorCommands"/> rather than reproducing its steps.
/// A test that migrated and imported in its own code would prove that migrating
/// and importing works; it would prove nothing about the executable a deploy
/// actually invokes, and the exit codes the compose stack branches on would be
/// untested.
/// </remarks>
public sealed class MigratorCommandTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "migrator_tests";

    /// <summary>Every test here starts from an empty database and drives the job itself.</summary>
    protected override bool Migrate => false;

    protected override bool ImportContent => false;

    [DockerFact]
    public async Task Migrate_BringsAnEmptyDatabaseUpToDate()
    {
        var exitCode = await Run("migrate");

        exitCode.ShouldBe(MigratorCommands.Success);

        await using var database = Database.CreateContext();

        (await database.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        (await database.ContentItems.CountAsync()).ShouldBe(0, "migrate does not import");
    }

    [DockerFact]
    public async Task All_MigratesThenImports()
    {
        var exitCode = await Run("all");

        exitCode.ShouldBe(MigratorCommands.Success);

        await using var database = Database.CreateContext();

        (await database.ContentItems.CountAsync()).ShouldBe(ContentFixture.ExpectedTotal);
        (await database.ContentReferences.CountAsync()).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Running the job again is safe, which is what makes a retried deploy safe.
    /// </summary>
    [DockerFact]
    public async Task All_RunTwiceSucceedsAndLeavesTheSameCatalogue()
    {
        (await Run("all")).ShouldBe(MigratorCommands.Success);

        var before = await IdentitiesAsync();

        (await Run("all")).ShouldBe(MigratorCommands.Success);

        (await IdentitiesAsync()).ShouldBe(before);
    }

    /// <summary>
    /// Importing before the schema exists fails with a code, rather than
    /// throwing out of the process.
    /// </summary>
    /// <remarks>
    /// The distinction matters to whatever runs the job: an unhandled exception
    /// and a non-zero exit both stop a compose deploy, but only one of them puts
    /// a readable line in the log first.
    /// </remarks>
    [DockerFact]
    public async Task Import_BeforeMigrateFailsWithoutThrowing()
    {
        var exitCode = await Run("import");

        exitCode.ShouldBe(MigratorCommands.Failed);
    }

    /// <summary>
    /// An import that finds no content fails the deploy rather than publishing
    /// an empty catalogue.
    /// </summary>
    /// <remarks>
    /// The only realistic way to reach this is a content volume that did not
    /// mount. Succeeding would leave the API serving nothing while every health
    /// check reported green, so the job refuses — and, because the importer
    /// never deletes on an empty scan, whatever was already there is still
    /// there afterwards.
    /// </remarks>
    [DockerFact]
    public async Task Import_FromADirectoryWithNoContentFailsAndChangesNothing()
    {
        (await Run("all")).ShouldBe(MigratorCommands.Success);

        var before = await IdentitiesAsync();

        using var empty = TempCorpus.Empty();

        // The same database, reached through services whose content root holds
        // nothing — which is what an unmounted volume looks like from inside
        // the job.
        await using var pointedAtNothing = Database.WithContentRoot(empty.Root);

        var exitCode = await MigratorCommands.RunAsync(
            pointedAtNothing.Services, "import", NullLogger.Instance);

        exitCode.ShouldBe(MigratorCommands.Failed);

        (await IdentitiesAsync()).ShouldBe(
            before, "a failed import must not have emptied the catalogue on its way out");
    }

    [DockerTheory]
    [InlineData("")]
    [InlineData("migrate-and-import")]
    [InlineData("--help")]
    [InlineData("drop")]
    public async Task AnUnrecognisedCommandIsRefusedWithItsOwnExitCode(string command)
    {
        var exitCode = await Run(command);

        exitCode.ShouldBe(MigratorCommands.UnknownCommand);

        // Nothing was applied on the way to refusing it.
        await using var database = Database.CreateContext();
        (await database.Database.GetAppliedMigrationsAsync()).ShouldBeEmpty();
    }

    private Task<int> Run(string command) =>
        MigratorCommands.RunAsync(Database.Services, command, NullLogger.Instance);

    private async Task<List<string>> IdentitiesAsync()
    {
        await using var database = Database.CreateContext();

        var rows = await database.ContentItems
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.ContentType, item.ItemKey, item.Version })
            .ToListAsync();

        return [.. rows.Select(row => $"{row.Id}:{row.ContentType}/{row.ItemKey}:{row.Version}")];
    }
}
