using System.Net;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class ContentItemEndpointTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    [Fact]
    public async Task Item_ReturnsTheWholeDocument()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species/wookiee");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("type").ShouldBe("species");
        body.Text("key").ShouldBe("wookiee");
        body.Text("name").ShouldBe("Wookiee");

        var data = body.GetProperty("data");
        data.Text("homeworld").ShouldBe("Kashyyyk");
        data.Text("nativeLanguage").ShouldBe("Shyriiwook");
        data.Array("traits").First().Text("name").ShouldBe("Powerful Build");
    }

    /// <summary>
    /// Sources name their display field "title" rather than "name". If the
    /// per-type projection ever loses that, sources index with an empty name
    /// and drop out of every list and every search.
    /// </summary>
    [Fact]
    public async Task Item_UsesThePerTypeDisplayField()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/source/phb"));

        body.Text("name").ShouldBe("Player's Handbook");
    }

    /// <summary>
    /// An enhanced item comes back whole, with the fields that make it a type
    /// of its own rather than a variant of equipment.
    /// </summary>
    /// <remarks>
    /// The published contract is that the body is the document exactly as its
    /// JSON Schema defines it, so the assertion is on the schema's own field
    /// names. The absences matter as much as the values: an enhanced item has
    /// no price and no weight, which is precisely why folding these 1,918 rows
    /// into equipment would have made eleven of that type's fields conditional.
    /// </remarks>
    [Fact]
    public async Task Item_ReturnsAnEnhancedItemWithItsRarityAndAttunement()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/enhanced-items/ghostfire-crystal");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("type").ShouldBe("enhanced-item");
        body.Text("name").ShouldBe("Ghostfire Crystal");

        var data = body.GetProperty("data");

        data.Text("itemType").ShouldBe("itemModification");
        data.Text("rarity").ShouldBe("legendary");
        data.GetProperty("requiresAttunement").GetBoolean().ShouldBeFalse();
        data.Text("subtype").ShouldBe("lightweapon");

        data.TryGetProperty("costInCredits", out _).ShouldBeFalse();
        data.TryGetProperty("weight", out _).ShouldBeFalse();
    }

    /// <summary>
    /// A rule comes back as the whole passage, not as a summary of one.
    /// </summary>
    /// <remarks>
    /// A chapter is reproduced whole rather than split into a document per
    /// heading, because the scrape did not preserve a heading hierarchy
    /// reliably enough to split on and because searching one page for a
    /// half-remembered rule is the thing readers do with it. The length check
    /// is what says the body survived the round trip rather than being
    /// truncated to the projected summary.
    /// </remarks>
    [Fact]
    public async Task Item_ReturnsARuleChapterInFull()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/rules/phb-using-ability-scores");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("name").ShouldBe("Using Ability Scores");

        var data = body.GetProperty("data");

        data.Text("ruleType").ShouldBe("chapter");
        data.GetProperty("chapterNumber").GetInt32().ShouldBe(7);

        var text = data.Text("body");

        text.Length.ShouldBeGreaterThan(30_000);
        text.ShouldContain("saving throw proficiencies");
    }

    [Fact]
    public async Task Item_ReturnsNotFoundForAnAbsentKey()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species/ewok");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        body.Text("title").ShouldBe("Content not found");
    }

    [Fact]
    public async Task Item_ReturnsNotFoundForAnUnknownType()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/starships/x-wing");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A key that exists under one type must not be reachable under another.
    /// Both halves matter: without the positive case, a repository that
    /// returned nothing for everything would pass.
    /// </summary>
    [Fact]
    public async Task Item_DoesNotLeakAcrossTypes()
    {
        var client = factory.CreateClient();

        (await client.GetAsync("/api/content/source/phb")).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/api/content/species/phb")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Item_IsCacheableAndValidated()
    {
        var client = factory.CreateClient();
        var first = await client.GetAsync("/api/content/species/wookiee");

        first.Headers.ETag.ShouldNotBeNull();
        first.Headers.CacheControl!.Public.ShouldBeTrue();

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get, "/api/content/species/wookiee");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        var second = await client.SendAsync(conditional);

        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        second.Headers.ETag.ShouldNotBeNull("a 304 that drops the validator makes the next request unconditional again");
    }

    /// <summary>
    /// Two different items must not share a validator.
    /// </summary>
    [Fact]
    public async Task Item_ETagsDifferBetweenItems()
    {
        var client = factory.CreateClient();

        var wookiee = await client.GetAsync("/api/content/species/wookiee");
        var human = await client.GetAsync("/api/content/species/human");

        wookiee.Headers.ETag!.Tag.ShouldNotBe(human.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Item_ErrorsNeverDiscloseTheFilesystem()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species/no-such-species");
        var raw = await response.Content.ReadAsStringAsync();

        raw.ShouldNotContain("TestContent");
        raw.ShouldNotContain(".json");
        raw.ShouldNotContain("Sw5e.Infrastructure");
        raw.ShouldNotContain("Exception");
        raw.ShouldNotContain("   at ");
    }
}
