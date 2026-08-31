using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// What the importer does to a database, run against a real one.
/// </summary>
public sealed class ContentImporterTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "importer_tests";

    /// <summary>Imports are performed by the tests themselves; each starts empty.</summary>
    protected override bool ImportContent => false;

    [DockerFact]
    public async Task Import_LoadsEveryValidDocument()
    {
        var result = await Database.ImportAsync();

        result.Inserted.ShouldBe(ContentFixture.ExpectedTotal);
        result.Updated.ShouldBe(0);
        result.Unchanged.ShouldBe(0);
        result.Deleted.ShouldBe(0);

        await using var database = Database.CreateContext();

        var counts = await database.ContentItems
            .GroupBy(item => item.ContentType)
            .Select(group => new { Type = group.Key, Count = group.Count() })
            .ToListAsync();

        counts.Select(row => $"{row.Type}={row.Count}").OrderBy(text => text, StringComparer.Ordinal)
              .ShouldBe(ContentFixture.ExpectedCounts
                            .Select(entry => $"{entry.Key}={entry.Value}")
                            .OrderBy(text => text, StringComparer.Ordinal));
    }

    /// <summary>
    /// The fixture holds one document with no display name. It must be skipped
    /// and reported, not imported as a nameless row.
    /// </summary>
    [DockerFact]
    public async Task Import_SkipsADocumentItCannotProjectAndSaysSo()
    {
        var result = await Database.ImportAsync();

        result.Warnings.ShouldContain(
            warning => warning.Contains("not-a-background") && warning.Contains("name"));

        await using var database = Database.CreateContext();

        (await database.ContentItems.AnyAsync(item => item.ItemKey == "not-a-background"))
            .ShouldBeFalse();
    }

    /// <summary>
    /// Running the importer again over an unchanged corpus must be a no-op.
    /// </summary>
    /// <remarks>
    /// The counts alone are not enough to prove that. An importer that deleted
    /// every row and re-inserted it would also report the right totals if it
    /// counted carelessly, and it would still have churned every row, bumped
    /// every version and invalidated every cached response in front of the API.
    /// So this asserts on the row identities and timestamps as well: the same
    /// ids, the same versions, and not one <c>updated_at</c> moved.
    /// </remarks>
    [DockerFact]
    public async Task Import_RunAgainOverTheSameCorpusChangesNothingAtAll()
    {
        await Database.ImportAsync();

        var before = await SnapshotAsync();

        var second = await Database.ImportAsync();

        second.Inserted.ShouldBe(0);
        second.Updated.ShouldBe(0);
        second.Deleted.ShouldBe(0);
        second.Unchanged.ShouldBe(ContentFixture.ExpectedTotal);

        var after = await SnapshotAsync();

        after.ShouldBe(before);
    }

    [DockerFact]
    public async Task Import_UpdatesOnlyTheDocumentThatChanged()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);
        var before = await SnapshotAsync();

        corpus.Edit("species", "wookiee", "life debts outlast empires", "life debts outlive empires");

        var result = await Database.ImportAsync(corpus.Root);

        result.Updated.ShouldBe(1);
        result.Inserted.ShouldBe(0);
        result.Deleted.ShouldBe(0);
        result.Unchanged.ShouldBe(ContentFixture.ExpectedTotal - 1);

        var after = await SnapshotAsync();

        // Exactly one row moved, and it is the one whose file was edited.
        var moved = after.Except(before).ToArray();
        moved.Length.ShouldBe(1);
        moved[0].ShouldStartWith("species/wookiee:");

        await using var database = Database.CreateContext();

        var wookiee = await database.ContentItems.SingleAsync(
            item => item.ContentType == "species" && item.ItemKey == "wookiee");

        // The projected columns are rebuilt from the new document, not left
        // behind at the old value.
        wookiee.SearchText.ShouldContain("outlive");
        wookiee.SearchText.ShouldNotContain("outlast");
        wookiee.SearchTextLower.ShouldContain("outlive");
        wookiee.Body.ShouldContain("outlive");
    }

    [DockerFact]
    public async Task Import_RemovesAnItemTheCorpusNoLongerHolds()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);

        corpus.Remove("species", "zabrak");

        var result = await Database.ImportAsync(corpus.Root);

        result.Deleted.ShouldBe(1);
        result.Inserted.ShouldBe(0);
        result.Updated.ShouldBe(0);

        await using var database = Database.CreateContext();

        (await database.ContentItems.AnyAsync(item => item.ItemKey == "zabrak")).ShouldBeFalse();
        (await database.ContentItems.CountAsync()).ShouldBe(ContentFixture.ExpectedTotal - 1);
    }

    /// <summary>
    /// An import that finds nothing must not delete anything.
    /// </summary>
    /// <remarks>
    /// This is the failure the importer exists to survive. An unmounted volume,
    /// a mistyped path and a genuinely empty corpus are indistinguishable from
    /// inside the importer, and the first two are far more likely than the
    /// third. A "mirror the directory" importer would empty the catalogue and
    /// report success, and the site would serve nothing until someone noticed.
    /// </remarks>
    [DockerFact]
    public async Task Import_FromAnEmptyDirectoryLeavesTheCatalogueIntact()
    {
        await Database.ImportAsync();

        using var empty = TempCorpus.Empty();

        var result = await Database.ImportAsync(empty.Root);

        result.Deleted.ShouldBe(0);
        result.Warnings.ShouldContain(warning => warning.Contains("no items"));

        await using var database = Database.CreateContext();

        (await database.ContentItems.CountAsync()).ShouldBe(ContentFixture.ExpectedTotal);
    }

    /// <summary>
    /// The same argument, one type at a time: a type that produced no files was
    /// not emptied, it was not read.
    /// </summary>
    [DockerFact]
    public async Task Import_FromACorpusMissingATypeLeavesThatTypeAlone()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);

        corpus.RemoveType("monster");

        var result = await Database.ImportAsync(corpus.Root);

        result.Deleted.ShouldBe(0);
        result.Warnings.ShouldContain(warning => warning.Contains("'monster'"));

        await using var database = Database.CreateContext();

        (await database.ContentItems.CountAsync(item => item.ContentType == "monster")).ShouldBe(1);
    }

    /// <summary>
    /// The document has to survive the round trip through jsonb with its nested
    /// structure intact, because the API returns it verbatim as the response
    /// body.
    /// </summary>
    /// <remarks>
    /// Asserting that the body is non-empty would pass against a store that
    /// wrote <c>{}</c>. The monster is the deepest document in the fixture, so
    /// it is what catches a body that was flattened, truncated or serialised
    /// through a DTO on the way in.
    /// </remarks>
    [DockerFact]
    public async Task Import_StoresTheDocumentWithItsNestingIntact()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var monster = await database.ContentItems.SingleAsync(
            item => item.ContentType == "monster" && item.ItemKey == "womp-rat");

        using var document = System.Text.Json.JsonDocument.Parse(monster.Body);
        var root = document.RootElement;

        root.GetProperty("abilities").GetProperty("dexterity").GetProperty("modifier").GetInt32()
            .ShouldBe(2);

        var behaviours = root.GetProperty("behaviors").EnumerateArray().ToArray();
        behaviours.Length.ShouldBe(1);
        behaviours[0].GetProperty("damageRoll").GetString().ShouldBe("1d4+2");

        root.GetProperty("types").EnumerateArray().Select(type => type.GetString())
            .ShouldBe(["beast"]);

        // The empty array must survive as an empty array rather than becoming
        // null or disappearing.
        root.GetProperty("languages").GetArrayLength().ShouldBe(0);
    }

    /// <summary>
    /// The projected columns are derived from the document, and they are what
    /// every list and search query filters and orders on.
    /// </summary>
    [DockerFact]
    public async Task Import_ProjectsTheRowColumnsOutOfTheDocument()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var power = await database.ContentItems.SingleAsync(
            item => item.ContentType == "power" && item.ItemKey == "force-push");

        power.Name.ShouldBe("Force Push");
        power.NameLower.ShouldBe("force push");
        power.SourceKey.ShouldBe("phb");
        power.ContentSet.ShouldBe("core");
        power.Summary.ShouldNotBeNull();
        power.Summary!.ShouldContain("telekinetic");
        power.SearchTextLower.ShouldContain("telekinetic force");

        // Parsed rather than matched as text. jsonb is a parsed representation,
        // not the string that was written to it: PostgreSQL returns members in
        // its own order with its own spacing, so an assertion on the literal
        // text would be asserting on jsonb's formatting rather than on the
        // projection.
        var facets = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(power.Facets)!;

        facets["powerType"].ShouldBe("force");
        facets["level"].ShouldBe("1");
        facets["concentration"].ShouldBe("false");
        power.Version.ShouldNotBeNullOrWhiteSpace();

        // The one type that names its display field "title" rather than "name"
        // must still land in the name column, or sources are unsortable and
        // unsearchable.
        var source = await database.ContentItems.SingleAsync(
            item => item.ContentType == "source" && item.ItemKey == "phb");

        source.Name.ShouldBe("Player's Handbook");

        // A source carries neither field: a book is the provenance rather than
        // having one. Both must land as null rather than as an empty string,
        // because the sort orders nulls last and an empty string first. This
        // used to be checked on a feature, which carried neither field until
        // the feature schema gained a source of its own.
        source.SourceKey.ShouldBeNull();
        source.ContentSet.ShouldBeNull();

        // And a feature does carry both now, derived from whatever grants it,
        // because the site refuses to publish an item it cannot attribute to a
        // book.
        var feature = await database.ContentItems.SingleAsync(
            item => item.ItemKey == "species-wookiee-powerful-build");

        feature.SourceKey.ShouldBe("phb");
        feature.ContentSet.ShouldBe("core");
    }

    /// <summary>
    /// Two items with the same version token must be byte-identical documents,
    /// and two different documents must not share one — that is the whole
    /// contract the ETag rests on.
    /// </summary>
    [DockerFact]
    public async Task Import_GivesEveryDistinctDocumentItsOwnVersion()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var versions = await database.ContentItems
            .Select(item => item.Version)
            .ToListAsync();

        versions.Count.ShouldBe(ContentFixture.ExpectedTotal);
        versions.Distinct().Count().ShouldBe(ContentFixture.ExpectedTotal);
    }

    /// <summary>
    /// Identity is (type, key). Nothing about the surrogate key may be allowed
    /// to shift when a document is merely updated, because the reference table
    /// points at it.
    /// </summary>
    [DockerFact]
    public async Task Import_KeepsAnItemsIdentityAcrossAnUpdate()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);

        long idBefore;

        await using (var database = Database.CreateContext())
        {
            idBefore = (await database.ContentItems.SingleAsync(
                item => item.ContentType == "species" && item.ItemKey == "wookiee")).Id;
        }

        corpus.Edit("species", "wookiee", "Kashyyyk, whose", "Kashyyyk, where");

        await Database.ImportAsync(corpus.Root);

        await using var reread = Database.CreateContext();

        var after = await reread.ContentItems.SingleAsync(
            item => item.ContentType == "species" && item.ItemKey == "wookiee");

        after.Id.ShouldBe(idBefore, "an update must not replace the row the graph points at");
        after.CreatedAt.ShouldBeLessThan(after.UpdatedAt);
    }

    /// <summary>
    /// Row identity, content version and last-changed time for every item, as
    /// one comparable set.
    /// </summary>
    /// <remarks>
    /// All three together, because each on its own can miss the thing that
    /// matters. Versions alone would not notice a delete-and-reinsert that
    /// produced the same hash; ids alone would not notice a rewritten body; and
    /// timestamps alone would not notice a row that was replaced within the
    /// same microsecond.
    /// </remarks>
    private async Task<List<string>> SnapshotAsync()
    {
        await using var database = Database.CreateContext();

        var rows = await database.ContentItems
            .OrderBy(item => item.ContentType)
            .ThenBy(item => item.ItemKey)
            .Select(item => new
            {
                item.ContentType,
                item.ItemKey,
                item.Version,
                item.Id,
                item.UpdatedAt,
            })
            .ToListAsync();

        return
        [
            .. rows.Select(row =>
                $"{row.ContentType}/{row.ItemKey}:{row.Version}@{row.Id}#{row.UpdatedAt:O}")
        ];
    }
}
