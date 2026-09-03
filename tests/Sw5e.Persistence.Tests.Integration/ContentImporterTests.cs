using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Sw5e.Infrastructure.Content;

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

    /// <summary>
    /// A document's version depends on how it is projected, not only on what it
    /// says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The importer decides whether to rewrite a row by comparing versions, so
    /// a version that is only a hash of the document makes a projection change
    /// invisible: the same bytes produce a different row, the hash does not
    /// move, and every unchanged document keeps a row built by the old rules.
    /// </para>
    /// <para>
    /// It happened. Harvesting Markdown headings into their own column imported
    /// cleanly against a database that had been running for days, reported
    /// "175 updated, 7,702 unchanged", and left the new column empty on 7,876
    /// of 7,877 rows. The tier that reads it did nothing, and every test here
    /// passed, because tests import into an empty database where every document
    /// is an insert and no version is ever compared.
    /// </para>
    /// <para>
    /// Asserted against the bare document hash rather than against a pinned
    /// value, so that this keeps meaning the same thing when the fixture
    /// changes. A pinned hash would have to be edited whenever anything moved,
    /// and a test that is routinely edited to make it pass stops being read.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheVersionOfADocumentCoversTheProjectionAsWellAsTheDocument()
    {
        using var document = JsonDocument.Parse("""{"key":"x","name":"X"}""");

        var version = ContentIndexBuilder.ComputeVersionFor(document.RootElement);

        var documentOnly = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(document.RootElement.GetRawText())))[..16];

        version.ShouldNotBe(documentOnly,
            "the version is a hash of the document alone, so a projection change " +
            "cannot invalidate a row the document did not touch");

        // And it is still a version: same shape, and stable across calls.
        version.Length.ShouldBe(16);
        version.ShouldBe(ContentIndexBuilder.ComputeVersionFor(document.RootElement));
    }

    /// <summary>
    /// Rows written under an earlier projection are rewritten, not skipped.
    /// </summary>
    /// <remarks>
    /// The mechanism the test above protects, exercised through the importer:
    /// every row is left holding a version from a projection that no longer
    /// exists, and the next import has to notice. Blanking the harvested column
    /// as well means the assertion is about the row being rebuilt rather than
    /// about a counter being incremented.
    /// </remarks>
    [DockerFact]
    public async Task Import_RewritesRowsLeftByAnEarlierProjection()
    {
        await Database.ImportAsync();

        int withHeadings;

        await using (var database = Database.CreateContext())
        {
            withHeadings = await database.ContentItems
                .CountAsync(item => item.HeadingTextLower != "");
        }

        withHeadings.ShouldBeGreaterThan(0,
            "the fixture has no headings at all, so this cannot detect losing them");

        // What a database looks like after a release that changed the
        // projection: rows built by the old rules, carrying old versions.
        await using (var database = Database.CreateContext())
        {
            await database.ContentItems.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.HeadingTextLower, "")
                .SetProperty(item => item.Version, "old-projection"));
        }

        var result = await Database.ImportAsync();

        result.Unchanged.ShouldBe(0, "rows from an earlier projection were skipped");
        result.Updated.ShouldBe(ContentFixture.ExpectedTotal);

        await using (var database = Database.CreateContext())
        {
            var rebuilt = await database.ContentItems
                .CountAsync(item => item.HeadingTextLower != "");

            rebuilt.ShouldBe(withHeadings);
        }
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
    }

    /// <summary>
    /// An empty collection survives as an empty collection rather than becoming
    /// null or disappearing.
    /// </summary>
    /// <remarks>
    /// Over an edited copy of the fixture rather than over the fixture itself,
    /// because no schema in the content repository permits an empty array —
    /// every one of them carries <c>minItems</c>, on the grounds that an empty
    /// list and an unfinished document look the same to a reader. So a document
    /// with one cannot be committed, and the exporter refuses to write one. The
    /// property is still worth pinning: it is a property of jsonb and of the
    /// importer, not of today's schemas, and the first schema to allow an empty
    /// list should not be the thing that discovers it.
    /// </remarks>
    [DockerFact]
    public async Task Import_KeepsAnEmptyCollectionAsAnEmptyCollection()
    {
        using var corpus = TempCorpus.FromFixture();

        var path = corpus.PathTo("monster", "womp-rat");

        // Line endings are normalised before the edit so the match does not
        // depend on how git checked the fixture out.
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var emptied = text.Replace(
            "\"languages\": [\n    \"None\"\n  ]", "\"languages\": []", StringComparison.Ordinal);

        emptied.ShouldNotBe(
            text, "the fixture's womp rat no longer holds the array this test empties");

        File.WriteAllText(path, emptied);

        await Database.ImportAsync(corpus.Root);

        await using var database = Database.CreateContext();

        var monster = await database.ContentItems.SingleAsync(
            item => item.ContentType == "monster" && item.ItemKey == "womp-rat");

        using var document = System.Text.Json.JsonDocument.Parse(monster.Body);

        document.RootElement.GetProperty("languages").ValueKind
                .ShouldBe(System.Text.Json.JsonValueKind.Array);

        document.RootElement.GetProperty("languages").GetArrayLength().ShouldBe(0);
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

    /// <summary>
    /// The authored reading path reaches the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The site builds its path from <c>readingGroup</c> and <c>order</c>, and
    /// deliberately never from <c>chapterNumber</c>: that field records where a
    /// passage fell in a printed book, and it disagrees with what a reader
    /// needs — the handbook numbers "What's Different?" below its own
    /// introduction. Projecting the two authored fields is what lets the site
    /// stop asking about the book at all.
    /// </para>
    /// <para>
    /// <c>chapterNumber</c> is asserted alongside them on purpose. It stays
    /// projected because it is true about the archive, and a test that only
    /// checked the new fields would not notice it being dropped by somebody
    /// tidying up.
    /// </para>
    /// </remarks>
    [DockerFact]
    public async Task Import_ProjectsTheAuthoredReadingPath()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var chapter = await database.ContentItems.SingleAsync(
            item => item.ContentType == "rule" && item.ItemKey == "phb-classes");

        // Facets are stored as the jsonb text the projection produced.
        var facets = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            chapter.Facets)!;

        facets["readingGroup"].GetString().ShouldBe("Creating a character");

        /*
          A string, not a number. Facets are a flat map of display values and
          every one of them is stringified on the way in, which is why the site
          parses `order` rather than reading it as an integer. Asserting the
          string here rather than quietly parsing it is the point: a test that
          coerced would hide the shape a client actually has to handle.
        */
        facets["order"].GetString().ShouldBe("5");

        // Still carried, and still not what the site navigates by.
        facets["chapterNumber"].GetString().ShouldBe("3");
    }

    /// <summary>
    /// Changing what a document is projected from means changing the version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the guard for the mistake that actually happened. The importer
    /// skips a document whose version it already holds, so a projection change
    /// reaches only the documents that were edited in the same release. When
    /// headings were harvested into their own column, the release imported
    /// cleanly, reported "175 updated, 7,702 unchanged", and left the new
    /// column empty on 7,876 of 7,877 rows. The feature was inert, and every
    /// test in this suite was green — because they all import into an empty
    /// database, where every document is an insert and no version is compared.
    /// </para>
    /// <para>
    /// Mixing the projection version into the document hash fixed the
    /// mechanism. It did not stop anybody forgetting to change it, which is the
    /// half that failed. Pinning both together does: edit the projection table
    /// and this fails, naming the version that has to move with it.
    /// </para>
    /// <para>
    /// It fingerprints the table and nothing else. It will not notice a change
    /// in how a field becomes text — the heading harvest, the summary cap — and
    /// that limit is stated in <c>ContentProjection.Fingerprint</c> rather than
    /// left to be discovered. What it removes is the case where the change is
    /// right there in the diff as an edited list, and the version two lines
    /// above it was simply not looked at.
    /// </para>
    /// </remarks>
    [Fact]
    public void ChangingTheProjectionMeansChangingItsVersion()
    {
        ContentProjection.Fingerprint().ShouldBe(
            "9959357939fa3903",
            "the projection table changed. Bump ContentProjection.Version and " +
            "put the new fingerprint here, or every document already in a " +
            "database keeps a row built by the old rules and the change reaches " +
            "nothing but whatever happens to be edited alongside it.");

        ContentProjection.Version.ShouldBe("3-reading-path");
    }
}