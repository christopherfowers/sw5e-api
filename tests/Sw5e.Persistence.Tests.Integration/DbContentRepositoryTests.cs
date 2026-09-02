using Shouldly;
using Sw5e.Domain.Content;

// Shouldly defines a SortDirection of its own, so the domain's is aliased
// rather than left ambiguous.
using SortDirection = Sw5e.Domain.Content.SortDirection;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Behaviour of the database-backed store that the parity comparison cannot
/// establish, because it is behaviour the two stores are allowed to implement
/// differently or that only one of them can get wrong.
/// </summary>
public sealed class DbContentRepositoryTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "repository_tests";

    private static ContentTypeDefinition Type(string key) =>
        ContentTypeRegistry.TryResolve(key, out var definition)
            ? definition
            : throw new ArgumentException($"'{key}' is not a content type.", nameof(key));

    private static ContentListQuery List(
        string type,
        int page = 1,
        int pageSize = 25,
        string? name = null,
        string? source = null,
        string? contentSet = null,
        ContentSortField sortBy = ContentSortField.Name,
        SortDirection direction = SortDirection.Ascending) =>
        new(Type(type), name, source, contentSet, sortBy, direction, page, pageSize);

    /// <summary>
    /// A key that is not a slug is answered with "no such item", not with a
    /// database error.
    /// </summary>
    /// <remarks>
    /// The endpoint rejects these before the store is asked, so this is the
    /// store's own guard rather than the one users hit. It matters because the
    /// column carries a check constraint on the same pattern: a key that is not
    /// a slug can never name a row, and turning that into an exception would
    /// give a caller a way to distinguish "refused" from "not found" and to
    /// generate error noise at will.
    /// </remarks>
    [DockerTheory]
    [InlineData("../source/phb")]
    [InlineData("..\\source\\phb")]
    [InlineData("wookiee.json")]
    [InlineData("Wookiee")]
    [InlineData("wookiee' OR '1'='1")]
    [InlineData("")]
    public async Task Get_AnswersNothingForAKeyThatIsNotASlug(string key)
    {
        var document = await Database.Repository.GetAsync(Type("species"), key);

        document.ShouldBeNull();

        // The paired positive case: the target of those traversals is a real,
        // reachable document, so a store that resolved one of them would have
        // returned something rather than nothing.
        (await Database.Repository.GetAsync(Type("source"), "phb")).ShouldNotBeNull();
    }

    [DockerFact]
    public async Task List_APageBeyondTheEndIsEmptyAndStillReportsTheTotal()
    {
        var result = await Database.Repository.ListAsync(List("species", page: 99, pageSize: 2));

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(ContentFixture.ExpectedCounts["species"]);
        result.Page.ShouldBe(99);
    }

    /// <summary>
    /// The total describes the filtered set, not the page.
    /// </summary>
    /// <remarks>
    /// Returning the page length instead is the classic pagination bug, and in
    /// a database implementation it is the specific mistake of counting after
    /// LIMIT rather than before it. The UI shows one page instead of three and
    /// nothing looks broken.
    /// </remarks>
    [DockerFact]
    public async Task List_CountsTheFilteredSetRatherThanThePage()
    {
        var result = await Database.Repository.ListAsync(List("species", page: 1, pageSize: 2));

        result.Items.Count.ShouldBe(2);
        result.TotalCount.ShouldBe(ContentFixture.ExpectedCounts["species"]);
    }

    /// <summary>
    /// Two different pages of the same list must not share a validator.
    /// </summary>
    [DockerFact]
    public async Task List_VersionDistinguishesPagesAndFilters()
    {
        var versions = new List<string>
        {
            (await Database.Repository.ListAsync(List("species", page: 1, pageSize: 2))).Version,
            (await Database.Repository.ListAsync(List("species", page: 2, pageSize: 2))).Version,
            (await Database.Repository.ListAsync(List("species", pageSize: 2, name: "w"))).Version,
            (await Database.Repository.ListAsync(List("species", pageSize: 2, source: "phb"))).Version,
            (await Database.Repository.ListAsync(
                List("species", pageSize: 2, direction: SortDirection.Descending))).Version,
            (await Database.Repository.ListAsync(List("power", page: 1, pageSize: 2))).Version,
        };

        versions.Distinct().Count().ShouldBe(versions.Count);
    }

    [DockerFact]
    public async Task List_VersionIsStableWhenNothingChanges()
    {
        var first = await Database.Repository.ListAsync(List("species"));
        var second = await Database.Repository.ListAsync(List("species"));

        second.Version.ShouldBe(first.Version);
    }

    /// <summary>
    /// Editing a document on a page must change that page's validator.
    /// </summary>
    /// <remarks>
    /// This is what a validator built only from the query and the total would
    /// get wrong: neither changes when a row's body is edited, so every client
    /// holding a cached page would be told for the next five minutes that its
    /// stale copy was current. Mixing each row's own content version into the
    /// token is what makes the edit visible.
    /// </remarks>
    [DockerFact]
    public async Task List_VersionChangesWhenARowOnThePageIsEdited()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);
        var before = await Database.Repository.ListAsync(List("species"));

        corpus.Edit("species", "wookiee", "life debts outlast empires", "life debts outlive empires");
        await Database.ImportAsync(corpus.Root);

        var after = await Database.Repository.ListAsync(List("species"));

        after.Version.ShouldNotBe(before.Version);

        // The rows themselves are the same rows in the same order; only the
        // content moved. Without this, the test would also pass if the edit had
        // dropped the item from the page entirely.
        after.Items.Select(item => item.Key).ShouldBe(before.Items.Select(item => item.Key));
    }

    [DockerFact]
    public async Task Get_VersionChangesWithTheDocumentAndNotOtherwise()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);

        var first = await Database.Repository.GetAsync(Type("species"), "wookiee");
        var unchanged = await Database.Repository.GetAsync(Type("species"), "wookiee");

        unchanged!.Version.ShouldBe(first!.Version);

        corpus.Edit("species", "wookiee", "Kashyyyk, whose", "Kashyyyk, where");
        await Database.ImportAsync(corpus.Root);

        var edited = await Database.Repository.GetAsync(Type("species"), "wookiee");

        edited!.Version.ShouldNotBe(first.Version);

        // Re-importing the same corpus again must leave the token alone, or
        // every deploy invalidates every client's cache.
        await Database.ImportAsync(corpus.Root);

        (await Database.Repository.GetAsync(Type("species"), "wookiee"))!.Version
            .ShouldBe(edited.Version);
    }

    /// <summary>
    /// Search version tokens follow the query and the documents behind it.
    /// </summary>
    [DockerFact]
    public async Task Search_VersionDistinguishesQueriesAndFollowsContent()
    {
        using var corpus = TempCorpus.FromFixture();

        await Database.ImportAsync(corpus.Root);

        var wookiee = await Database.Repository.SearchAsync(new ContentSearchQuery("wookiee", null, 5));
        var force = await Database.Repository.SearchAsync(new ContentSearchQuery("force", null, 5));
        var narrower = await Database.Repository.SearchAsync(new ContentSearchQuery("wookiee", null, 1));

        new[] { wookiee.Version, force.Version, narrower.Version }.Distinct().Count().ShouldBe(3);

        corpus.Edit("species", "wookiee", "life debts outlast empires", "life debts outlive empires");
        await Database.ImportAsync(corpus.Root);

        (await Database.Repository.SearchAsync(new ContentSearchQuery("wookiee", null, 5)))
            .Version.ShouldNotBe(wookiee.Version);
    }

    /// <summary>
    /// An empty catalogue is answered, not failed.
    /// </summary>
    /// <remarks>
    /// A migrated database with nothing in it is the state between the schema
    /// landing and the first import, and it is what the API is pointed at
    /// during a deploy. Every endpoint has to work: the registry lists every
    /// type at zero, lists are empty pages, and search finds nothing.
    /// </remarks>
    [DockerFact]
    public async Task AnEmptyCatalogueIsServedRatherThanFailed()
    {
        await using var empty = await Fixture.CreateDatabaseAsync("empty_catalogue");
        await empty.MigrateAsync();

        var types = await empty.Repository.GetContentTypesAsync();

        types.Count.ShouldBe(ContentTypeRegistry.All.Count);
        types.ShouldAllBe(type => type.ItemCount == 0);

        var listed = await empty.Repository.ListAsync(List("species"));
        listed.Items.ShouldBeEmpty();
        listed.TotalCount.ShouldBe(0);

        var found = await empty.Repository.SearchAsync(new ContentSearchQuery("wookiee", null, 5));
        found.TotalMatches.ShouldBe(0);
        found.Groups.ShouldBeEmpty();

        (await empty.Repository.GetAsync(Type("species"), "wookiee")).ShouldBeNull();
    }

    /// <summary>
    /// A search phrase full of SQL punctuation is a phrase, not syntax.
    /// </summary>
    /// <remarks>
    /// The search query is the only hand-written SQL in the application, so it
    /// is the only place this could go wrong. Every value in it is a parameter;
    /// these confirm that from the outside, and that the endpoint answers
    /// rather than erroring, which is what a caller probing for an injection
    /// point would be looking for.
    /// </remarks>
    /// <summary>
    /// A phrase that names a section is a heading match, not a prose match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case this tier was added for, and it was found on the deployed site
    /// rather than here. Searching "difficult terrain" returned one hundred and
    /// twenty-two matches with the rules chapter — which has a section titled
    /// "Difficult Terrain" — in fifth place, behind twenty-nine class features
    /// that mention the phrase in passing. Every hit had landed in the same
    /// tier, so ranking collapsed to the alphabet inside whichever content type
    /// happened to have the most matches.
    /// </para>
    /// <para>
    /// "Aging Thresholds" is a heading in the committed fixture and appears
    /// nowhere else in it, so a document matching on it can only have matched
    /// through the heading column.
    /// </para>
    /// </remarks>
    [DockerFact]
    public async Task Search_ReportsAHeadingMatchAsAHeading()
    {
        var result = await Database.Repository.SearchAsync(
            new ContentSearchQuery("aging thresholds", null, 5));

        var hit = result.Groups
            .Single(group => group.Type == "rule")
            .Hits.Single(candidate => candidate.Item.Key == "aging");

        hit.MatchedField.ShouldBe(SearchMatchField.Heading);

        // And it still shows the phrase in context. A heading's words are part
        // of the prose the window is cut from, so there is no reason for this
        // tier to carry less evidence than the one below it — a result that
        // asserts a match without showing one makes somebody open the page to
        // find out whether it was worth opening.
        hit.Snippet.ShouldNotBeNullOrWhiteSpace();
        hit.Snippet.ShouldContain("Aging Thresholds", Case.Insensitive);
    }

    /// <summary>
    /// A heading outranks prose, and prose still reports itself as prose.
    /// </summary>
    /// <remarks>
    /// Both phrases are in the same document, so nothing about the two
    /// documents can explain the difference in score: the only thing that
    /// varies is where the phrase was found.
    /// </remarks>
    [DockerFact]
    public async Task Search_ScoresAHeadingAboveTheProseAroundIt()
    {
        var heading = await Database.Repository.SearchAsync(
            new ContentSearchQuery("aging thresholds", null, 5));

        var prose = await Database.Repository.SearchAsync(
            new ContentSearchQuery("campaigns might take place", null, 5));

        var byHeading = heading.Groups
            .Single(group => group.Type == "rule")
            .Hits.Single(candidate => candidate.Item.Key == "aging");

        var byProse = prose.Groups
            .Single(group => group.Type == "rule")
            .Hits.Single(candidate => candidate.Item.Key == "aging");

        byHeading.MatchedField.ShouldBe(SearchMatchField.Heading);
        byProse.MatchedField.ShouldBe(SearchMatchField.Text);

        byHeading.Score.ShouldBeGreaterThan(byProse.Score);
    }

    [DockerTheory]
    [InlineData("'; DROP TABLE content.content_item; --")]
    [InlineData("' OR 1=1 --")]
    [InlineData("100% cotton")]
    [InlineData("under_score")]
    [InlineData("back\\slash")]
    [InlineData("\"quoted\"")]
    public async Task Search_TreatsPunctuationAsTextRatherThanAsSyntax(string phrase)
    {
        var result = await Database.Repository.SearchAsync(new ContentSearchQuery(phrase, null, 5));

        result.Query.ShouldBe(phrase);
        result.TotalMatches.ShouldBe(0);

        // The catalogue is still there afterwards, which is the assertion that
        // makes the first case mean something.
        var types = await Database.Repository.GetContentTypesAsync();
        types.Sum(type => type.ItemCount).ShouldBe(ContentFixture.ExpectedTotal);
    }
}
