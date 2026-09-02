using Shouldly;
using Sw5e.Domain.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Search over the whole corpus has to put the right document first.
/// </summary>
/// <remarks>
/// <para>
/// The gap this fills is the one that let a ranking regression reach the
/// deployed site. Every other search test in this project runs against the 321
/// document fixture, where a query matches a handful of documents and any
/// ordering looks reasonable. Relevance is not visible at that size. It only
/// appears at the size where a phrase matches a hundred documents and the
/// question stops being "was it found" and becomes "was it found first".
/// </para>
/// <para>
/// So these run against the corpus at the pinned submodule commit, and they
/// assert the two things a reader would notice: the document a phrase is about
/// comes before the documents that merely mention it, and a large result set is
/// ordered by something other than the alphabet.
/// </para>
/// <para>
/// Both assertions are in one class and share one import for the reason the
/// round-trip test gives: importing 7,877 documents is the expensive part, and
/// xUnit would do it once per test class.
/// </para>
/// </remarks>
public sealed class ContentSearchRelevanceTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "search_relevance";

    /// <summary>The corpus is imported here, not the fixture.</summary>
    protected override bool ImportContent => false;

    private async Task<ContentSearchResult> SearchTheCorpusAsync(string phrase, int maxPerType = 25)
    {
        Directory.Exists(ContentFixture.CommittedCorpus).ShouldBeTrue(
            $"No corpus at '{ContentFixture.CommittedCorpus}'. Initialise the submodule with " +
            "'git submodule update --init'.");

        var imported = await Database.ImportAsync(ContentFixture.CommittedCorpus);

        // A partial import would leave every assertion below passing over
        // whatever fraction happened to load.
        imported.Inserted.ShouldBeGreaterThan(
            7000, "the whole corpus should have imported, not part of it");

        return await Database.Repository.SearchAsync(
            new ContentSearchQuery(phrase, null, maxPerType));
    }

    /// <summary>
    /// The chapter with a section on the phrase comes before the class features
    /// that mention it in passing, and a long tail is genuinely ranked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "difficult terrain" is the query the regression was reported on. It
    /// matched a hundred and twenty-two documents; the Adventuring chapter,
    /// which has a section by that name, came fifth, behind twenty-nine class
    /// features. Nothing was broken in the sense of throwing — every one of
    /// those documents does contain the phrase. They were simply all given the
    /// same score, and once every score is equal the only thing left to sort by
    /// is the tiebreak, which is the name.
    /// </para>
    /// <para>
    /// "poison damage" is the same failure with none of the mitigation: it
    /// matches over a hundred documents and not one of them has it as a
    /// heading, so before this change the entire result set shared a single
    /// score and the reader got an alphabetical list of everything in the game
    /// that mentions poison.
    /// </para>
    /// </remarks>
    [DockerFact]
    public async Task TheDocumentAPhraseIsAboutOutranksTheDocumentsThatMentionIt()
    {
        var result = await SearchTheCorpusAsync("difficult terrain");

        // The regression was cross-group as well as within one: the rules
        // chapter lost to the type that happened to have the most matches.
        result.TotalMatches.ShouldBeGreaterThan(50);
        result.Groups[0].Type.ShouldBe("rule");

        var rules = result.Groups[0].Hits;
        rules[0].MatchedField.ShouldBe(SearchMatchField.Heading);

        // Named rather than positioned. Two chapters carry a "Difficult
        // Terrain" section — Combat, where it is a movement rule, and
        // Adventuring — and which of the two is more about it is a judgement
        // about the corpus that the corpus is entitled to change. That both
        // outrank a class feature mentioning the phrase once is not, and that
        // these two are the only sections named after it is a fact about the
        // corpus worth pinning: a third would mean the harvester changed.
        rules.Where(hit => hit.MatchedField == SearchMatchField.Heading)
             .Select(hit => hit.Item.Key)
             .ShouldBe(["phb-combat", "phb-adventuring"], ignoreOrder: true);

        // The property the tier exists for, across the whole result rather than
        // inside one group: a section named after the phrase always beats prose
        // that happens to contain it.
        var hits = result.Groups.SelectMany(group => group.Hits).ToList();

        var weakestHeading = hits.Where(hit => hit.MatchedField == SearchMatchField.Heading)
                                 .Min(hit => hit.Score);
        var strongestProse = hits.Where(hit => hit.MatchedField == SearchMatchField.Text)
                                 .Max(hit => hit.Score);

        weakestHeading.ShouldBeGreaterThan(strongestProse);
    }

    /// <summary>
    /// A tier holding a hundred documents orders them by relevance rather than
    /// by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted as tie density, which is the thing that actually broke. When
    /// every document in a tier is given the same number, ordering falls
    /// through to the tiebreak, and the tiebreak is the name — so the reader
    /// gets an alphabetical list of everything in the game that mentions
    /// poison. The fix is not that some particular document comes first; it is
    /// that the scores carry enough information to order by at all.
    /// </para>
    /// <para>
    /// Restricted to prose matches on purpose. A weaker version of this test
    /// looked at every hit in the group and asked whether the names were in
    /// alphabetical order, and it passed against the bug: two different tiers
    /// interleaved are never alphabetical overall, however flat each tier is
    /// inside itself. Measuring the largest set of hits sharing one score is
    /// what distinguishes a ranked list from a sorted one.
    /// </para>
    /// <para>
    /// Written as a proportion rather than a count so that it says the same
    /// thing whatever the corpus grows to.
    /// </para>
    /// </remarks>
    [DockerFact]
    public async Task ALargeResultSetIsRankedRatherThanSorted()
    {
        var result = await SearchTheCorpusAsync("poison damage");

        var largest = result.Groups.MaxBy(group => group.TotalMatches)!;
        largest.TotalMatches.ShouldBeGreaterThan(20);

        var prose = largest.Hits
            .Where(hit => hit.MatchedField == SearchMatchField.Text)
            .ToList();

        prose.Count.ShouldBeGreaterThan(10,
            "there are not enough prose matches here for this to be measuring anything");

        var largestTie = prose.GroupBy(hit => hit.Score).Max(group => group.Count());

        largestTie.ShouldBeLessThan(prose.Count / 2,
            $"{largestTie} of {prose.Count} prose matches share a single score, so their " +
            "order is the tiebreak's rather than the ranking's");
    }
}
