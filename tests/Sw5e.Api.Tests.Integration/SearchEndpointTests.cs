using System.Net;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class SearchEndpointTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    [Fact]
    public async Task Search_FindsAnItemByName()
    {
        var response = await factory.CreateClient().GetAsync("/api/search?q=wookiee");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("query").ShouldBe("wookiee");

        var species = body.Array("groups").Single(group => group.Text("type") == "species");
        var hit = species.Array("results").Single();

        hit.GetProperty("item").Text("key").ShouldBe("wookiee");
        hit.Text("matchedIn").ShouldBe("name");
    }

    /// <summary>
    /// A group carries the labels a heading needs and the route segment the
    /// site links with, so a result list can be rendered without a second call
    /// to the registry.
    /// </summary>
    [Fact]
    public async Task Search_GroupsCarryTheirDisplayLabels()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=wookiee"));

        var species = body.Array("groups").Single(group => group.Text("type") == "species");

        species.Text("name").ShouldBe("Species");
        species.Text("pluralName").ShouldBe("Species");
        species.Text("routeSegment").ShouldBe("species");
    }

    /// <summary>
    /// A row must be renderable straight from the result, without fetching the
    /// item. If the projection is ever dropped from the hit, this is what
    /// catches it.
    /// </summary>
    [Fact]
    public async Task Search_ResultsCarryEnoughToRenderARow()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=force%20push"));

        var item = body.Array("groups")
            .Single(group => group.Text("type") == "power")
            .Array("results").First()
            .GetProperty("item");

        item.Text("name").ShouldBe("Force Push");
        item.Text("sourceKey").ShouldBe("phb");
        item.Text("contentSet").ShouldBe("core");
        item.Text("summary").ShouldNotBeEmpty();
        item.GetProperty("facets").Text("powerType").ShouldBe("force");
    }

    /// <summary>
    /// The match explanation is the point of the endpoint: a hit that comes
    /// from body prose has to say so and show the phrase, or the user cannot
    /// tell why the row is in front of them. "outlast" appears only inside the
    /// Wookiee lore, never in a name, a key or a display field.
    /// </summary>
    [Fact]
    public async Task Search_ExplainsABodyMatchAndQuotesIt()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=outlast"));

        var hit = body.Array("groups")
            .Single(group => group.Text("type") == "species")
            .Array("results")
            .Single(result => result.GetProperty("item").Text("key") == "wookiee");

        hit.Text("matchedIn").ShouldBe("text");
        hit.Text("snippet").ShouldContain("outlast");
        hit.Text("snippet").ShouldContain("life debts");
    }

    /// <summary>
    /// A display field is matched ahead of the prose that repeats it, because
    /// "homeworld: Kashyyyk" is a better explanation than a sentence that
    /// happens to mention the word.
    /// </summary>
    [Fact]
    public async Task Search_PrefersADisplayFieldOverTheProseThatRepeatsIt()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=kashyyyk"));

        var hit = body.Array("groups")
            .Single(group => group.Text("type") == "species")
            .Array("results").Single();

        hit.GetProperty("item").Text("key").ShouldBe("wookiee");
        hit.Text("matchedIn").ShouldBe("facet");
        hit.Text("matchedField").ShouldBe("homeworld");
    }

    /// <summary>
    /// A match on a display field names the field, so the UI can label it
    /// rather than presenting a bare fragment.
    /// </summary>
    [Fact]
    public async Task Search_NamesTheFieldForADisplayFieldMatch()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=iridonia"));

        var hit = body.Array("groups")
            .Single(group => group.Text("type") == "species")
            .Array("results").Single();

        hit.GetProperty("item").Text("key").ShouldBe("zabrak");
        hit.Text("matchedIn").ShouldBe("facet");
        hit.Text("matchedField").ShouldBe("homeworld");
    }

    /// <summary>
    /// Results are grouped by type rather than returned flat. "kinetic" appears
    /// in a weapon's damage type, a creature's bite, and the prose of a power,
    /// so a working search returns three groups from one query.
    /// </summary>
    [Fact]
    public async Task Search_GroupsMatchesAcrossTypes()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=kinetic"));

        body.Array("groups").Select(group => group.Text("type"))
            .ShouldBe(["equipment", "monster", "power"], ignoreOrder: true);
    }

    /// <summary>
    /// A name match outranks a mention in someone else's prose. Ordering is the
    /// difference between a useful search box and a list the user reads through.
    /// </summary>
    /// <remarks>
    /// The two matching powers are chosen so that relevance and alphabetical
    /// order disagree: "Battle Meditation" only mentions shielding in its
    /// description, and sorts first by name. An implementation that ranked
    /// alphabetically, or that did not rank at all, would put it in front.
    /// </remarks>
    [Fact]
    public async Task Search_RanksNameMatchesAboveBodyMatches()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=shield"));

        var powers = body.Array("groups").Single(group => group.Text("type") == "power");

        powers.Array("results").Select(result => result.GetProperty("item").Text("key"))
            .ShouldBe(["energy-shield", "battle-meditation"]);

        powers.Array("results").First().Text("matchedIn").ShouldBe("name");
        powers.Array("results").Last().Text("matchedIn").ShouldBe("text");
    }

    /// <summary>
    /// Each group reports how many items of its type matched, not how many were
    /// returned, so the UI can offer "see all 3" and link through to the
    /// filtered list.
    /// </summary>
    [Fact]
    public async Task Search_ReportsTotalMatchesBeyondTheReturnedSlice()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=phb&limit=1"));

        var species = body.Array("groups").Single(group => group.Text("type") == "species");

        species.Array("results").Count().ShouldBe(1);
        species.GetProperty("totalMatches").GetInt32().ShouldBe(5);
    }

    /// <summary>
    /// A phrase 35,000 characters into a chapter is findable.
    /// </summary>
    /// <remarks>
    /// This is the case the rule type exists for: a reader half-remembers a
    /// passage and searches for it. The phrase below sits about 35,000
    /// characters into the fixture chapter, well past the 16,000-character
    /// ceiling the search index used to carry, and the chapter is not split
    /// into per-heading documents — so an index that truncated the body would
    /// return nothing here and look exactly like a passage that was never
    /// written.
    /// </remarks>
    [Fact]
    public async Task Search_FindsAPassageDeepInsideARuleChapter()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=saving%20throw%20proficiencies"));

        var hit = body.Array("groups")
            .Single(group => group.Text("type") == "rule")
            .Array("results")
            .Single();

        hit.GetProperty("item").Text("key").ShouldBe("phb-using-ability-scores");
        hit.Text("matchedIn").ShouldBe("text");
        hit.Text("snippet").ShouldContain("saving throw proficiencies");
    }

    /// <summary>
    /// A glossary entry is findable by the name an equipment row prints, which
    /// is the lookup the whole type exists to answer.
    /// </summary>
    [Fact]
    public async Task Search_FindsAPropertyGlossaryEntryByItsPrintedName()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=power%20cell"));

        var hit = body.Array("groups")
            .Single(group => group.Text("type") == "weapon-property")
            .Array("results")
            .Single();

        hit.GetProperty("item").Text("key").ShouldBe("power-cell");
        hit.Text("matchedIn").ShouldBe("name");
    }

    /// <summary>
    /// A word that appears only inside a markdown table is searchable.
    /// </summary>
    /// <remarks>
    /// Reference tables are nothing but a pipe table, so if the flattening
    /// dropped cell boundaries instead of collapsing them to spaces, the first
    /// and last words of adjacent cells would be fused into tokens no reader
    /// would ever type. "Comfortable" is a cell of its own in the lifestyle
    /// table and appears nowhere else in the fixture.
    /// </remarks>
    [Fact]
    public async Task Search_FindsAWordThatIsAWholeCellOfAMarkdownTable()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=comfortable"));

        var hit = body.Array("groups")
            .Single(group => group.Text("type") == "reference-table")
            .Array("results")
            .Single();

        hit.GetProperty("item").Text("key").ShouldBe("lifestyle-expenses");
        hit.Text("matchedIn").ShouldBe("text");
    }

    [Fact]
    public async Task Search_RestrictsToTheRequestedTypes()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=kinetic&types=monster"));

        body.Array("groups").Select(group => group.Text("type")).ShouldBe(["monster"]);
    }

    /// <summary>
    /// Naming every registered type is a request the API must accept, because
    /// it asks for exactly what an unfiltered search already returns.
    /// </summary>
    /// <remarks>
    /// The bound on the parameter is the size of the registry, so this is the
    /// test that fails if the two ever part company — which is what a
    /// hard-coded count would guarantee the first time a type was added. The
    /// paired over-limit case keeps the bound from being no bound at all.
    /// </remarks>
    [Fact]
    public async Task Search_AcceptsEveryRegisteredTypeAtOnceAndRefusesOneMore()
    {
        var client = factory.CreateClient();

        var registry = await JsonResponse.ReadAsync(await client.GetAsync("/api/content-types"));
        var keys = registry.Array("types").Select(type => type.Text("key")).ToArray();

        // A floor rather than an exact count. Content types arrive from several
        // work streams at once, and an exact number here would mean every one
        // of them editing this line — which is a merge conflict, not a test.
        // What this assertion is for is the pairing below: the endpoint has to
        // accept as many types as the registry actually holds, whatever that
        // number has grown to, and refuse one more.
        keys.Length.ShouldBeGreaterThanOrEqualTo(20);

        // Named explicitly, because "twenty or more" would still pass if the
        // five types this branch adds had quietly failed to register.
        keys.ShouldContain("enhanced-item");
        keys.ShouldContain("weapon-property");
        keys.ShouldContain("armor-property");
        keys.ShouldContain("rule");
        keys.ShouldContain("reference-table");

        var all = await client.GetAsync($"/api/search?q=core&types={string.Join(',', keys)}");

        all.StatusCode.ShouldBe(HttpStatusCode.OK);

        // One name past the limit. It repeats a type rather than inventing one,
        // so a 400 here can only be the count and not an unknown-type refusal.
        var overLimit = await client.GetAsync(
            $"/api/search?q=core&types={string.Join(',', keys)},{keys[0]}");
        var problem = await JsonResponse.ReadAsync(overLimit);

        overLimit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.Text("title").ShouldBe("Too many content types");
    }

    [Fact]
    public async Task Search_ReturnsAnEmptyResultRatherThanAnErrorForNoMatches()
    {
        var response = await factory.CreateClient().GetAsync("/api/search?q=zzzzzznothing");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("totalMatches").GetInt32().ShouldBe(0);
        body.Array("groups").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/search?q=")]
    [InlineData("/api/search?q=%20%20")]
    [InlineData("/api/search?q=a")]
    public async Task Search_RejectsAMissingOrTooShortQuery(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Search_RejectsAnOversizedQuery()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/search?q=" + new string('a', 101));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_AcceptsTheLongestPermittedQuery()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/search?q=" + new string('a', 100));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(26)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Search_RejectsAnOutOfRangeLimit(int limit)
    {
        var response = await factory.CreateClient().GetAsync($"/api/search?q=wookiee&limit={limit}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_IsCacheableAndValidated()
    {
        var client = factory.CreateClient();
        var first = await client.GetAsync("/api/search?q=wookiee");

        first.Headers.ETag.ShouldNotBeNull();

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/search?q=wookiee");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        (await client.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    /// <summary>
    /// Two different searches must not share a validator.
    /// </summary>
    [Fact]
    public async Task Search_ETagDistinguishesQueries()
    {
        var client = factory.CreateClient();

        var wookiee = await client.GetAsync("/api/search?q=wookiee");
        var kinetic = await client.GetAsync("/api/search?q=kinetic");

        wookiee.Headers.ETag!.Tag.ShouldNotBe(kinetic.Headers.ETag!.Tag);
    }
}
