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
