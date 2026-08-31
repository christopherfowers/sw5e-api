using Microsoft.EntityFrameworkCore;
using Shouldly;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The content graph: which links the importer extracts, which of them resolve,
/// and what happens to the ones that do not.
/// </summary>
/// <remarks>
/// This is the part of the schema that justifies putting the catalogue in a
/// database rather than in files, so it is the part with the most to get wrong.
/// The fixture is built so that every interesting case is present: a link by
/// slug, a link by display name, a link whose target has not been written, a
/// link whose target type does not exist as a content type at all, and a link
/// buried in a prose sentence beside conditions that are not links.
/// </remarks>
public sealed class ContentReferenceTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "reference_tests";

    protected override bool ImportContent => false;

    /// <summary>
    /// Every item that declares a source gets an edge to it, and every one of
    /// those resolves, because all three sources are in the fixture.
    /// </summary>
    /// <remarks>
    /// The count is asserted exactly. "At least one source edge" would pass
    /// against an importer that extracted the first item's source and then
    /// stopped, which is the shape a broken loop takes.
    /// </remarks>
    [DockerFact]
    public async Task Import_RecordsAndResolvesTheSourceOfEveryItemThatDeclaresOne()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var sourceEdges = await database.ContentReferences
            .Where(reference => reference.Relation == "source")
            .Include(reference => reference.FromItem)
            .ToListAsync();

        // Twenty-four of the twenty-eight fixture documents declare a
        // sourceKey. Four do not: the three sources, which are the provenance
        // rather than carrying one, and the deliberately nameless background
        // that never gets imported at all. Features used to be a fifth
        // exception — the schema had no field for it — and are not any more: a
        // feature is printed inside whatever grants it, so it is in the same
        // book, and it now says so.
        sourceEdges.Count.ShouldBe(24);
        sourceEdges.ShouldAllBe(edge => edge.ResolvedItemId != null);
        sourceEdges.ShouldAllBe(edge => edge.TargetKind == ContentReferenceTargetKind.Key);

        sourceEdges.Select(edge => edge.FromItem!.ContentType).Distinct()
                   .OrderBy(type => type, StringComparer.Ordinal)
                   .ShouldBe([
                       "archetype", "background", "class", "class-improvement", "equipment",
                       "feat", "feature", "lightsaber-form", "maneuver", "monster", "power",
                       "species", "weapon-focus"
                   ]);
    }

    /// <summary>
    /// A feature names the thing that grants it by display name, and the
    /// grantor's type comes from a second field on the same document.
    /// </summary>
    [DockerTheory]
    [InlineData("archetype-soresu-form-deflection-3", "archetype", "Soresu Form", "soresu-form")]
    [InlineData("species-wookiee-powerful-build", "species", "Wookiee", "wookiee")]
    public async Task Import_ResolvesAFeatureToWhateverGrantsIt(
        string featureKey,
        string grantorType,
        string grantorName,
        string grantorKey)
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var edge = await database.ContentReferences
            .Include(reference => reference.ResolvedItem)
            .SingleAsync(reference =>
                reference.Relation == "grantedBy" &&
                reference.FromItem!.ItemKey == featureKey);

        edge.TargetType.ShouldBe(grantorType);
        edge.TargetKind.ShouldBe(ContentReferenceTargetKind.Name);
        edge.TargetIdentifier.ShouldBe(grantorName);

        // Resolved to the actual row, not merely recorded. The feature's key
        // embeds the grantor's slug and its field names the grantor's display
        // name; only one of those is what the edge was built from, and this is
        // what says the join found the right row either way.
        edge.ResolvedItem.ShouldNotBeNull();
        edge.ResolvedItem!.ItemKey.ShouldBe(grantorKey);
        edge.ResolvedItem.ContentType.ShouldBe(grantorType);
    }

    /// <summary>
    /// Two different types name their class the same way, and both edges land
    /// on the same class row.
    /// </summary>
    /// <remarks>
    /// This edge used to be the corpus's only record that classes existed at
    /// all: it was recorded and left dangling, because nothing had authored the
    /// class type yet. Now that a class is a real item, the same rule has to
    /// resolve rather than merely record — and it has to do so from an
    /// archetype and from a class improvement alike, since the class page is
    /// the only route to either of them.
    /// </remarks>
    [DockerFact]
    public async Task Import_ResolvesAnArchetypeAndAnImprovementToTheSameClass()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var edges = await database.ContentReferences
            .Include(reference => reference.ResolvedItem)
            .Where(reference => reference.Relation == "class")
            .OrderBy(reference => reference.Id)
            .ToListAsync();

        edges.Count.ShouldBe(2);

        foreach (var edge in edges)
        {
            edge.TargetType.ShouldBe("class");
            edge.TargetIdentifier.ShouldBe("Guardian");
            edge.JsonPath.ShouldBe("$.className");
            edge.ResolvedItem.ShouldNotBeNull();
            edge.ResolvedItem!.ItemKey.ShouldBe("guardian");
            edge.ResolvedItem.ContentType.ShouldBe("class");
        }

        // Both edges resolve to one row, which is what makes "everything that
        // belongs to the guardian" a single query rather than a name match.
        edges.Select(edge => edge.ResolvedItemId).Distinct().Count().ShouldBe(1);
    }

    /// <summary>
    /// One power's prerequisite names a power that exists and another names one
    /// that does not. Both are recorded; only the first resolves.
    /// </summary>
    /// <remarks>
    /// The pair is the point. A test with only the resolving case would pass
    /// against an importer that discarded anything it could not resolve, and a
    /// test with only the dangling case would pass against one that recorded
    /// everything and resolved nothing.
    /// </remarks>
    [DockerFact]
    public async Task Import_RecordsAPowerPrerequisiteWhetherOrNotItResolves()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var edges = await database.ContentReferences
            .Where(reference => reference.Relation == "prerequisitePower")
            .Include(reference => reference.FromItem)
            .Include(reference => reference.ResolvedItem)
            .OrderBy(reference => reference.FromItem!.ItemKey)
            .ToListAsync();

        edges.Count.ShouldBe(2);

        var throwEdge = edges.Single(edge => edge.FromItem!.ItemKey == "force-throw");
        throwEdge.TargetIdentifier.ShouldBe("Force Push");
        throwEdge.ResolvedItem.ShouldNotBeNull();
        throwEdge.ResolvedItem!.ItemKey.ShouldBe("force-push");

        var shatterEdge = edges.Single(edge => edge.FromItem!.ItemKey == "mind-shatter");
        shatterEdge.TargetIdentifier.ShouldBe("Mind Trap");
        shatterEdge.ResolvedItemId.ShouldBeNull();
    }

    /// <summary>
    /// An unresolved reference is reported, precisely enough to act on.
    /// </summary>
    [DockerFact]
    public async Task Import_ReportsEveryUnresolvedReference()
    {
        var result = await Database.ImportAsync();

        // Two in the fixture: the power that requires a power nobody has
        // written, and the background's third feat option. The archetype's
        // class used to be a third, and resolves now that classes are content.
        result.ReferencesUnresolved.ShouldBe(2);

        result.Warnings.ShouldContain(
            warning => warning.Contains("Mind Trap") && warning.Contains("$.prerequisite"));

        result.Warnings.ShouldContain(
            warning => warning.Contains("Ace Pilot") && warning.Contains("$.featOptions[2].name"));
    }

    /// <summary>
    /// A maneuver declares two different edges to other maneuvers, and they are
    /// not the same edge written twice.
    /// </summary>
    /// <remarks>
    /// The prerequisite is the gate and names the tier immediately below;
    /// <c>improves</c> names the base maneuver the chain hangs off. For a third
    /// tier those are different documents — Administer Aid (Greater) requires
    /// Administer Aid (Improved) and improves Administer Aid — so an extractor
    /// that treated one as a synonym for the other would publish a chain that
    /// skips a tier. The fixture holds Riposte and Riposte (Improved), where
    /// the two edges happen to agree, which is why the assertions check the
    /// relations separately rather than counting edges.
    /// </remarks>
    [DockerFact]
    public async Task Import_RecordsBothAManeuversGateAndTheManeuverItUpgrades()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var edges = await database.ContentReferences
            .Include(reference => reference.FromItem)
            .Include(reference => reference.ResolvedItem)
            .Where(reference => reference.FromItem!.ItemKey == "riposte-improved")
            .ToListAsync();

        var gate = edges.Single(edge => edge.Relation == "prerequisiteManeuver");
        gate.TargetType.ShouldBe("maneuver");
        gate.TargetIdentifier.ShouldBe("Riposte");
        gate.ResolvedItem!.ItemKey.ShouldBe("riposte");

        var upgrade = edges.Single(edge => edge.Relation == "improvesManeuver");
        upgrade.JsonPath.ShouldBe("$.improves");
        upgrade.TargetIdentifier.ShouldBe("Riposte");
        upgrade.ResolvedItem!.ItemKey.ShouldBe("riposte");

        // The base maneuver has neither, and a maneuver with no prerequisite
        // must not produce an edge to a maneuver called "" or to itself.
        (await database.ContentReferences.CountAsync(
            reference => reference.FromItem!.ItemKey == "riposte" &&
                         reference.Relation != "source"))
            .ShouldBe(0);
    }

    /// <summary>
    /// A feat prerequisite is a sentence that mixes a level requirement with a
    /// feat name. Only the feat is a link.
    /// </summary>
    /// <remarks>
    /// The negative half matters more than the positive one here. An extractor
    /// that split on commas and took every clause would produce an edge to a
    /// feat called "4th level", which would never resolve and would sit in the
    /// unresolved report forever as noise that hides the real gaps.
    /// </remarks>
    [DockerFact]
    public async Task Import_TakesTheFeatOutOfAPrerequisiteAndLeavesTheRest()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var edges = await database.ContentReferences
            .Where(reference => reference.Relation == "prerequisiteFeat")
            .Include(reference => reference.ResolvedItem)
            .ToListAsync();

        edges.Count.ShouldBe(1, "'4th level' is a condition, not a link");
        edges[0].TargetIdentifier.ShouldBe("Durable");
        edges[0].ResolvedItem!.ItemKey.ShouldBe("durable");

        // Sharpshooter's prerequisite is "Dexterity 13 or higher", which names
        // no feat at all and must produce no edge.
        (await database.ContentReferences.AnyAsync(
            reference => reference.Relation == "prerequisiteFeat" &&
                         reference.FromItem!.ItemKey == "sharpshooter"))
            .ShouldBeFalse();
    }

    /// <summary>
    /// A background's feat options keep the order the roll table gives them.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing: the position in the list is the number a player
    /// rolls. An edge set that came back in an arbitrary order would render a
    /// table whose rolls do not match the book.
    /// </remarks>
    [DockerFact]
    public async Task Import_KeepsABackgroundsFeatOptionsInTheirRolledOrder()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var options = await database.ContentReferences
            .Where(reference => reference.Relation == "featOption")
            .OrderBy(reference => reference.Ordinal)
            .ToListAsync();

        options.Select(option => option.TargetIdentifier)
               .ShouldBe(["Sharpshooter", "Durable", "Ace Pilot"]);

        options.Select(option => option.Ordinal).ShouldBe([0, 1, 2]);

        options.Select(option => option.JsonPath)
               .ShouldBe(["$.featOptions[0].name", "$.featOptions[1].name", "$.featOptions[2].name"]);

        // The first two are real feats; the third is not in the fixture.
        options[0].ResolvedItemId.ShouldNotBeNull();
        options[1].ResolvedItemId.ShouldNotBeNull();
        options[2].ResolvedItemId.ShouldBeNull();
    }

    /// <summary>
    /// The graph is traversable backwards, which is what the print pipeline
    /// needs: given a publication, collect everything printed in it.
    /// </summary>
    /// <remarks>
    /// Answered as one join rather than by fetching every document and reading
    /// a field out of each. That difference is the entire argument for the
    /// reference table, so it is worth a test that actually performs the
    /// traversal.
    /// </remarks>
    [DockerFact]
    public async Task References_AnswerWhatWasPublishedInOneSourceWithASingleJoin()
    {
        await Database.ImportAsync();

        await using var database = Database.CreateContext();

        var expandedContent = await database.ContentItems.SingleAsync(
            item => item.ContentType == "source" && item.ItemKey == "ec");

        var printedThere = await database.ContentReferences
            .Where(reference => reference.Relation == "source" &&
                                reference.ResolvedItemId == expandedContent.Id)
            .Include(reference => reference.FromItem)
            .Select(reference => reference.FromItem!.ContentType + "/" + reference.FromItem.ItemKey)
            .OrderBy(identity => identity)
            .ToListAsync();

        printedThere.ShouldBe([
            "class-improvement/guardian-multiclass-improvement",
            "monster/womp-rat",
            "power/mind-shatter",
            "species/zabrak",
        ]);
    }

    /// <summary>
    /// An edge that could not be resolved resolves later, when its target is
    /// finally written — without the document that declared it changing at all.
    /// </summary>
    /// <remarks>
    /// This is the test that pins the design down. Resolution is a property of
    /// the whole catalogue, not of the document the edge came from, so it has
    /// to be recomputed over every edge on every import. An importer that only
    /// resolved the edges of items it had just written would leave this link
    /// permanently broken: <c>force-throw.json</c> is byte-identical across
    /// both imports, so it is never touched, and its reference would never be
    /// looked at again.
    /// </remarks>
    [DockerFact]
    public async Task Import_ResolvesAnOldEdgeWhenItsTargetIsAddedLater()
    {
        using var corpus = TempCorpus.FromFixture();

        // Import a corpus that has the power requiring "Force Push" but not
        // Force Push itself.
        corpus.Remove("power", "force-push");

        var first = await Database.ImportAsync(corpus.Root);

        await using (var database = Database.CreateContext())
        {
            var dangling = await database.ContentReferences.SingleAsync(
                reference => reference.Relation == "prerequisitePower" &&
                             reference.TargetIdentifier == "Force Push");

            dangling.ResolvedItemId.ShouldBeNull();
        }

        // Now write the missing power. Nothing else in the corpus changes.
        File.Copy(
            Path.Combine(ContentFixture.Path, "power", "force-push.json"),
            corpus.PathTo("power", "force-push"));

        var second = await Database.ImportAsync(corpus.Root);

        second.Inserted.ShouldBe(1);
        second.Updated.ShouldBe(0, "no existing document changed");
        second.ReferencesUnresolved.ShouldBe(first.ReferencesUnresolved - 1);

        await using var reread = Database.CreateContext();

        var resolved = await reread.ContentReferences
            .Include(reference => reference.ResolvedItem)
            .SingleAsync(reference => reference.Relation == "prerequisitePower" &&
                                      reference.TargetIdentifier == "Force Push");

        resolved.ResolvedItem.ShouldNotBeNull();
        resolved.ResolvedItem!.ItemKey.ShouldBe("force-push");
    }

    /// <summary>
    /// Changing a document rewrites its edges rather than adding to them.
    /// </summary>
    [DockerFact]
    public async Task Import_ReplacesAnItemsEdgesWhenItsDocumentChanges()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);

        corpus.Edit("power", "mind-shatter", "\"prerequisite\": \"Mind Trap\"", "\"prerequisite\": \"Force Push\"");

        await Database.ImportAsync(corpus.Root);

        await using var database = Database.CreateContext();

        var edges = await database.ContentReferences
            .Where(reference => reference.FromItem!.ItemKey == "mind-shatter" &&
                                reference.Relation == "prerequisitePower")
            .ToListAsync();

        edges.Count.ShouldBe(1, "the old edge must be gone, not accompanied by the new one");
        edges[0].TargetIdentifier.ShouldBe("Force Push");
        edges[0].ResolvedItemId.ShouldNotBeNull();
    }

    /// <summary>
    /// Re-importing an unchanged corpus leaves the graph exactly as it was.
    /// </summary>
    /// <remarks>
    /// The item-level idempotence test says nothing about edges: an importer
    /// that skipped unchanged items but rebuilt every edge unconditionally
    /// would pass that one and fail this, and the symptom in production would be
    /// reference ids churning on every deploy.
    /// </remarks>
    [DockerFact]
    public async Task Import_RunAgainLeavesTheGraphUntouched()
    {
        await Database.ImportAsync();
        var before = await GraphSnapshotAsync();

        var second = await Database.ImportAsync();

        second.ReferencesWritten.ShouldBe(0, "nothing changed, so no edge needed rewriting");

        (await GraphSnapshotAsync()).ShouldBe(before);
    }

    private async Task<List<string>> GraphSnapshotAsync()
    {
        await using var database = Database.CreateContext();

        var rows = await database.ContentReferences
            .OrderBy(reference => reference.Id)
            .Select(reference => new
            {
                reference.Id,
                reference.FromItemId,
                reference.Relation,
                reference.JsonPath,
                reference.TargetType,
                reference.TargetIdentifier,
                reference.ResolvedItemId,
                reference.Ordinal,
            })
            .ToListAsync();

        return
        [
            .. rows.Select(row =>
                $"{row.Id}:{row.FromItemId}:{row.Relation}:{row.JsonPath}:" +
                $"{row.TargetType}:{row.TargetIdentifier}:{row.ResolvedItemId}:{row.Ordinal}")
        ];
    }
}
