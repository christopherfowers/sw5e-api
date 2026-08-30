using System.Text.Json;
using Shouldly;
using Sw5e.Domain.Content;

// Shouldly defines a SortDirection of its own, so the domain's is aliased
// rather than left ambiguous.
using SortDirection = Sw5e.Domain.Content.SortDirection;
using Sw5e.Infrastructure.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The two content stores must answer the same question the same way.
/// </summary>
/// <remarks>
/// <para>
/// Switching from the file-backed store to the database-backed one is meant to
/// be one registration change, which is only true if it is invisible: the same
/// request has to return the same rows, in the same order, with the same
/// totals, the same match explanations and the same snippets. Testing the two
/// stores separately against hand-written expectations cannot establish that —
/// two sets of expectations drift, and the drift is exactly the bug.
/// </para>
/// <para>
/// So every case here runs both stores over the same corpus and compares them
/// to each other. What that catches is the class of divergence a per-store test
/// cannot see: an <c>ORDER BY</c> that uses the database's locale collation
/// instead of byte order, a LIKE that treats a caller's percent sign as a
/// wildcard, a null that sorts first instead of last, a scoring tier evaluated
/// in a different order in SQL than in C#.
/// </para>
/// <para>
/// One thing is deliberately not compared: the version token. The two stores
/// compute validators differently on purpose — the file-backed store mixes in a
/// hash of the whole index, the database-backed one uses the versions of the
/// exact rows in the response — and requiring them to agree would mean giving
/// up the better of the two. ETags are opaque, and the interface says so.
/// </para>
/// </remarks>
public sealed class StoreParityTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "parity_tests";

    /// <summary>The file-backed store, reading the same fixture the database was imported from.</summary>
    private static readonly IContentRepository FileStore =
        FileContentRepository.Load(ContentFixture.Path).Repository;

    [DockerFact]
    public async Task ContentTypes_ReportTheSameCountsFromBothStores()
    {
        var fromFile = await FileStore.GetContentTypesAsync();
        var fromDatabase = await Database.Repository.GetContentTypesAsync();

        Describe(fromDatabase).ShouldBe(Describe(fromFile));

        // Anchored to the declared expectation as well, so the pair cannot
        // agree on being wrong.
        fromDatabase.Where(type => type.ItemCount > 0)
                    .Select(type => $"{type.Key}={type.ItemCount}")
                    .OrderBy(text => text, StringComparer.Ordinal)
                    .ShouldBe(ContentFixture.ExpectedCounts
                                  .Select(entry => $"{entry.Key}={entry.Value}")
                                  .OrderBy(text => text, StringComparer.Ordinal));

        static IEnumerable<string> Describe(IReadOnlyList<ContentTypeDescriptor> types) =>
            types.Select(type =>
                $"{type.Key}|{type.DisplayName}|{type.PluralName}|{type.RouteSegment}|{type.ItemCount}");
    }

    /// <summary>
    /// Every combination of type, ordering and direction, page by page.
    /// </summary>
    /// <remarks>
    /// Walked a page at a time rather than in one large page, because a paging
    /// difference between the stores only appears at a page boundary: two
    /// orderings that disagree about a tie return the same set in one page and
    /// different rows on page two.
    /// </remarks>
    [DockerTheory]
    [MemberData(nameof(ListCases))]
    public async Task List_ReturnsTheSameRowsInTheSameOrderFromBothStores(
        string type,
        ContentSortField sortBy,
        SortDirection direction,
        int pageSize)
    {
        var definition = Resolve(type);
        var seenFromFile = new List<string>();
        var seenFromDatabase = new List<string>();

        for (var page = 1; page <= 4; page++)
        {
            var query = new ContentListQuery(
                definition, null, null, null, sortBy, direction, page, pageSize);

            var fromFile = await FileStore.ListAsync(query);
            var fromDatabase = await Database.Repository.ListAsync(query);

            fromDatabase.TotalCount.ShouldBe(fromFile.TotalCount);
            fromDatabase.Page.ShouldBe(fromFile.Page);
            fromDatabase.PageSize.ShouldBe(fromFile.PageSize);

            Keys(fromDatabase).ShouldBe(
                Keys(fromFile),
                $"page {page} of {type} ordered by {sortBy} {direction}");

            seenFromFile.AddRange(Keys(fromFile));
            seenFromDatabase.AddRange(Keys(fromDatabase));
        }

        // Paging must partition the set: every item exactly once across the
        // pages walked, from both stores.
        seenFromDatabase.ShouldBe(seenFromFile);
        seenFromDatabase.Distinct().Count().ShouldBe(seenFromDatabase.Count);
        seenFromDatabase.Count.ShouldBe(ContentFixture.ExpectedCounts[type]);

        static List<string> Keys(PagedResult<ContentSummary> result) =>
            [.. result.Items.Select(item => item.Key)];
    }

    public static TheoryData<string, ContentSortField, SortDirection, int> ListCases()
    {
        var data = new TheoryData<string, ContentSortField, SortDirection, int>();

        foreach (var type in ContentFixture.ExpectedCounts.Keys)
        {
            foreach (var sortBy in Enum.GetValues<ContentSortField>())
            {
                foreach (var direction in Enum.GetValues<SortDirection>())
                {
                    // A page size smaller than every type, so every case
                    // crosses at least one page boundary.
                    data.Add(type, sortBy, direction, 2);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Ordering by a field two thirds of the corpus does not have.
    /// </summary>
    /// <remarks>
    /// Nulls are where the two stores are most likely to part company. .NET has
    /// no opinion about where a null sorts and the file-backed store supplies
    /// one; PostgreSQL has a default that differs between ascending and
    /// descending. Features carry neither a source nor a content set, so
    /// ordering the whole catalogue by either puts the two side by side.
    /// </remarks>
    [DockerTheory]
    [InlineData(ContentSortField.SourceKey, SortDirection.Ascending)]
    [InlineData(ContentSortField.SourceKey, SortDirection.Descending)]
    [InlineData(ContentSortField.ContentSet, SortDirection.Ascending)]
    [InlineData(ContentSortField.ContentSet, SortDirection.Descending)]
    public async Task List_PlacesRowsWithNoValueInTheSamePlaceInBothStores(
        ContentSortField sortBy,
        SortDirection direction)
    {
        var query = new ContentListQuery(
            Resolve("feature"), null, null, null, sortBy, direction, 1, 25);

        var fromFile = await FileStore.ListAsync(query);
        var fromDatabase = await Database.Repository.ListAsync(query);

        fromDatabase.Items.Select(item => item.Key)
                    .ShouldBe(fromFile.Items.Select(item => item.Key));

        // The fixture's features are the rows with no value at all, so this
        // case would be vacuous if they had one.
        fromFile.Items.ShouldAllBe(item => item.SourceKey == null && item.ContentSet == null);
    }

    [DockerTheory]
    [InlineData("species", "wook")]
    [InlineData("species", "WOOK")]
    [InlineData("species", "i")]
    [InlineData("species", "twi'")]
    [InlineData("power", "force")]
    [InlineData("power", "Force ")]
    [InlineData("equipment", "combat suit")]
    [InlineData("feat", "durable")]
    [InlineData("species", "no-such-species")]
    public async Task List_FiltersOnNameIdenticallyInBothStores(string type, string filter)
    {
        var query = new ContentListQuery(
            Resolve(type), filter, null, null,
            ContentSortField.Name, SortDirection.Ascending, 1, 25);

        var fromFile = await FileStore.ListAsync(query);
        var fromDatabase = await Database.Repository.ListAsync(query);

        fromDatabase.Items.Select(item => item.Key).ShouldBe(fromFile.Items.Select(item => item.Key));
        fromDatabase.TotalCount.ShouldBe(fromFile.TotalCount);
    }

    /// <summary>
    /// A LIKE metacharacter in the filter must match itself.
    /// </summary>
    /// <remarks>
    /// The obvious database implementation builds <c>'%' || filter || '%'</c>
    /// and hands it to LIKE, at which point a caller who searches for "%" gets
    /// the whole type back and a caller who searches for "_" gets every
    /// single-character name. The file-backed store treats the filter as a
    /// plain substring, so both must return nothing here: nothing in the corpus
    /// contains any of these characters. Asserting the two stores agree is not
    /// enough on its own — they would agree on returning everything — so the
    /// count is asserted outright.
    /// </remarks>
    [DockerTheory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("wook%")]
    [InlineData("w_okiee")]
    [InlineData("\\")]
    [InlineData("100%")]
    public async Task List_TreatsWildcardsInANameFilterAsOrdinaryCharacters(string filter)
    {
        var query = new ContentListQuery(
            Resolve("species"), filter, null, null,
            ContentSortField.Name, SortDirection.Ascending, 1, 25);

        var fromFile = await FileStore.ListAsync(query);
        var fromDatabase = await Database.Repository.ListAsync(query);

        fromFile.TotalCount.ShouldBe(0, "no species name contains this text literally");
        fromDatabase.TotalCount.ShouldBe(0);
        fromDatabase.Items.ShouldBeEmpty();
    }

    [DockerTheory]
    [InlineData("species", "phb", null)]
    [InlineData("species", "ec", null)]
    [InlineData("species", "PHB", null)]
    [InlineData("power", null, "core")]
    [InlineData("power", null, "expanded-content")]
    [InlineData("power", "ec", "expanded-content")]
    [InlineData("power", "phb", "expanded-content")]
    [InlineData("monster", "phb", null)]
    public async Task List_FiltersOnSourceAndContentSetIdenticallyInBothStores(
        string type,
        string? source,
        string? contentSet)
    {
        var query = new ContentListQuery(
            Resolve(type), null, source, contentSet,
            ContentSortField.Name, SortDirection.Ascending, 1, 25);

        var fromFile = await FileStore.ListAsync(query);
        var fromDatabase = await Database.Repository.ListAsync(query);

        fromDatabase.Items.Select(item => item.Key).ShouldBe(fromFile.Items.Select(item => item.Key));
        fromDatabase.TotalCount.ShouldBe(fromFile.TotalCount);
    }

    /// <summary>
    /// A row must render the same from either store, field for field.
    /// </summary>
    [DockerFact]
    public async Task List_ProjectsTheSameRowFieldsFromBothStores()
    {
        foreach (var type in ContentFixture.ExpectedCounts.Keys)
        {
            var query = new ContentListQuery(
                Resolve(type), null, null, null,
                ContentSortField.Key, SortDirection.Ascending, 1, 25);

            var fromFile = await FileStore.ListAsync(query);
            var fromDatabase = await Database.Repository.ListAsync(query);

            fromDatabase.Items.Select(Describe).ShouldBe(fromFile.Items.Select(Describe), type);
        }

        static string Describe(ContentSummary item) =>
            string.Join('|',
                item.Type,
                item.Key,
                item.Name,
                item.SourceKey ?? "<null>",
                item.ContentSet ?? "<null>",
                item.Summary ?? "<null>",
                string.Join(';', item.Facets.OrderBy(facet => facet.Key, StringComparer.Ordinal)
                                            .Select(facet => $"{facet.Key}={facet.Value}")));
    }

    /// <summary>
    /// Every document in the corpus is byte-for-byte equivalent from both
    /// stores, once the fact that jsonb does not preserve member order is
    /// accounted for.
    /// </summary>
    /// <remarks>
    /// Compared as canonicalised JSON rather than as raw text. jsonb stores a
    /// parsed value, so member order and whitespace are lost on the way in and
    /// the returned text differs from the file's — which is documented and
    /// intentional, and is not a difference any consumer of this API can
    /// observe, because JSON objects are unordered. Everything else about the
    /// document must survive exactly: every member, every nested array, every
    /// number, every empty collection.
    /// </remarks>
    [DockerFact]
    public async Task Get_ReturnsAnEquivalentDocumentFromBothStores()
    {
        var compared = 0;

        foreach (var type in ContentFixture.ExpectedCounts.Keys)
        {
            var definition = Resolve(type);

            var listing = await FileStore.ListAsync(new ContentListQuery(
                definition, null, null, null,
                ContentSortField.Key, SortDirection.Ascending, 1, 100));

            foreach (var row in listing.Items)
            {
                var fromFile = await FileStore.GetAsync(definition, row.Key);
                var fromDatabase = await Database.Repository.GetAsync(definition, row.Key);

                fromDatabase.ShouldNotBeNull($"{type}/{row.Key} is missing from the database");
                fromDatabase!.Type.ShouldBe(fromFile!.Type);
                fromDatabase.Key.ShouldBe(fromFile.Key);
                fromDatabase.Name.ShouldBe(fromFile.Name);

                Canonical(fromDatabase.Body).ShouldBe(
                    Canonical(fromFile.Body), $"{type}/{row.Key}");

                compared++;
            }
        }

        compared.ShouldBe(ContentFixture.ExpectedTotal);
    }

    [DockerTheory]
    [InlineData("species", "no-such-key")]
    [InlineData("power", "force-push-pull")]
    [InlineData("monster", "rancor")]
    public async Task Get_ReturnsNothingForAMissingItemFromBothStores(string type, string key)
    {
        var definition = Resolve(type);

        (await FileStore.GetAsync(definition, key)).ShouldBeNull();
        (await Database.Repository.GetAsync(definition, key)).ShouldBeNull();
    }

    /// <summary>
    /// Search must rank, group, explain and quote identically.
    /// </summary>
    /// <remarks>
    /// The phrases below are chosen to reach every tier of the scoring ladder:
    /// an exact name, a name prefix, a name substring, a slug, a display field,
    /// body prose, and a multi-word query whose words appear apart. If any of
    /// them stops reaching its tier, this test starts comparing two stores that
    /// agree because neither found anything, so the tiers are asserted
    /// explicitly below.
    /// </remarks>
    [DockerTheory]
    [InlineData("wookiee")]
    [InlineData("Force Push")]
    [InlineData("force")]
    [InlineData("push")]
    [InlineData("kashyyyk")]
    [InlineData("outlast")]
    [InlineData("telekinetic")]
    [InlineData("shatter")]
    [InlineData("soresu")]
    [InlineData("deflect blaster")]
    [InlineData("lightsaber outlast")]
    [InlineData("combat")]
    [InlineData("core")]
    [InlineData("no such phrase anywhere")]
    public async Task Search_ReturnsTheSameGroupsHitsAndSnippetsFromBothStores(string phrase)
    {
        var query = new ContentSearchQuery(phrase, null, 5);

        var fromFile = await FileStore.SearchAsync(query);
        var fromDatabase = await Database.Repository.SearchAsync(query);

        fromDatabase.Query.ShouldBe(fromFile.Query);
        fromDatabase.TotalMatches.ShouldBe(fromFile.TotalMatches, phrase);

        Describe(fromDatabase).ShouldBe(Describe(fromFile), phrase);

        static List<string> Describe(ContentSearchResult result) =>
        [
            .. result.Groups.SelectMany(group => group.Hits.Select(hit =>
                string.Join('|',
                    group.Type,
                    group.TotalMatches.ToString(),
                    hit.Item.Key,
                    hit.MatchedField.ToString(),
                    hit.MatchedFieldName ?? "<null>",
                    hit.Snippet,
                    Math.Round(hit.Score, 2).ToString("F2"))))
        ];
    }

    /// <summary>
    /// The phrases above genuinely exercise every tier.
    /// </summary>
    /// <remarks>
    /// Without this, the parity theory could silently degrade into comparing
    /// two empty results. It asserts on the file-backed store alone, because
    /// that is the definition the database implementation was written against.
    /// </remarks>
    [DockerTheory]
    [InlineData("wookiee", "wookiee", SearchMatchField.Name)]
    [InlineData("kashyyyk", "wookiee", SearchMatchField.Facet)]
    [InlineData("outlast", "wookiee", SearchMatchField.Text)]
    [InlineData("soresu-form", "soresu-form", SearchMatchField.Key)]
    [InlineData("lightsaber outlast", "soresu-form", SearchMatchField.Text)]
    public async Task Search_TheParityPhrasesReachTheTiersTheyAreMeantTo(
        string phrase,
        string expectedKey,
        SearchMatchField expectedField)
    {
        var result = await FileStore.SearchAsync(new ContentSearchQuery(phrase, null, 5));

        var hit = result.Groups
            .SelectMany(group => group.Hits)
            .SingleOrDefault(candidate => candidate.Item.Key == expectedKey);

        hit.ShouldNotBeNull($"'{phrase}' should have matched {expectedKey}");
        hit!.MatchedField.ShouldBe(expectedField);
    }

    /// <summary>
    /// Search cuts each group to the limit while still reporting how many
    /// matched, from both stores.
    /// </summary>
    [DockerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(25)]
    public async Task Search_CutsEachGroupToTheSameSizeInBothStores(int maxPerType)
    {
        // "core" appears in the content set of most of the corpus, so it
        // matches across several types and every group is bigger than the cut.
        var query = new ContentSearchQuery("core", null, maxPerType);

        var fromFile = await FileStore.SearchAsync(query);
        var fromDatabase = await Database.Repository.SearchAsync(query);

        fromDatabase.TotalMatches.ShouldBe(fromFile.TotalMatches);

        fromDatabase.Groups.Select(group => $"{group.Type}:{group.TotalMatches}:{group.Hits.Count}")
                    .ShouldBe(fromFile.Groups.Select(group =>
                        $"{group.Type}:{group.TotalMatches}:{group.Hits.Count}"));

        fromDatabase.Groups.ShouldAllBe(group => group.Hits.Count <= maxPerType);

        if (maxPerType == 1)
        {
            // The cut is real: at least one group holds back more than it
            // returned, or this theory proves nothing about windowing.
            fromDatabase.Groups.ShouldContain(group => group.TotalMatches > group.Hits.Count);
        }
    }

    [DockerFact]
    public async Task Search_RestrictsToTheRequestedTypesInBothStores()
    {
        var types = new[] { Resolve("power"), Resolve("species") };
        var query = new ContentSearchQuery("force", types, 5);

        var fromFile = await FileStore.SearchAsync(query);
        var fromDatabase = await Database.Repository.SearchAsync(query);

        fromDatabase.Groups.Select(group => group.Type)
                    .ShouldBe(fromFile.Groups.Select(group => group.Type));

        fromDatabase.Groups.ShouldAllBe(group => group.Type == "power" || group.Type == "species");
        fromDatabase.Groups.ShouldNotBeEmpty();
    }

    private static ContentTypeDefinition Resolve(string type) =>
        ContentTypeRegistry.TryResolve(type, out var definition)
            ? definition
            : throw new ArgumentException($"'{type}' is not a content type.", nameof(type));

    /// <summary>
    /// Serialises a document with its members in a fixed order, so two
    /// documents that differ only in member order compare equal.
    /// </summary>
    private static string Canonical(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => $"{JsonSerializer.Serialize(property.Name)}:{Canonical(property.Value)}");

                return $"{{{string.Join(',', members)}}}";

            case JsonValueKind.Array:
                // Array order is significant and is preserved by jsonb.
                return $"[{string.Join(',', element.EnumerateArray().Select(Canonical))}]";

            default:
                return element.GetRawText();
        }
    }
}
