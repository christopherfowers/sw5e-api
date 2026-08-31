using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Sw5e.Domain.Content;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class ContentTypesEndpointTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    [Fact]
    public async Task ContentTypes_ListsEveryType()
    {
        var response = await factory.CreateClient().GetAsync("/api/content-types");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var keys = body.Array("types").Select(type => type.Text("key")).ToArray();

        keys.ShouldBe(
            [
                "source", "species", "background", "archetype", "feature", "feat", "power",
                "maneuver", "fighting-style", "fighting-mastery", "lightsaber-form",
                "weapon-focus", "weapon-supremacy",
                "equipment", "monster",
                "starship-base-size", "starship-deployment", "starship-equipment",
                "starship-modification", "starship-venture", "starship-rule",
                "credit-category", "credit", "asset-credit"
            ],
            ignoreOrder: false);
    }

    /// <summary>
    /// The site has published /maneuvers since before any maneuver content
    /// existed: the type is in its navigation and its index rendered empty.
    /// The registry's key is singular like every other one, so the plural has
    /// to come back as the route segment or the link the header already offers
    /// would point at an address the API does not answer on.
    /// </summary>
    [Fact]
    public async Task ContentTypes_GiveTheCombatOptionsTheRouteSegmentsTheSiteAlreadyPublishes()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content-types"));

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maneuver"] = "maneuvers",
            ["fighting-style"] = "fighting-styles",
            ["fighting-mastery"] = "fighting-masteries",
            ["lightsaber-form"] = "lightsaber-forms",
            ["weapon-focus"] = "weapon-focuses",
            ["weapon-supremacy"] = "weapon-supremacies",
        };

        foreach (var (key, routeSegment) in expected)
        {
            var type = body.Array("types").Single(entry => entry.Text("key") == key);

            type.Text("routeSegment").ShouldBe(routeSegment);
        }
    }

    [Fact]
    public async Task ContentTypes_CarryTheFieldsNavigationNeeds()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content-types"));

        var species = body.Array("types").Single(type => type.Text("key") == "species");

        species.Text("name").ShouldBe("Species");
        species.Text("pluralName").ShouldBe("Species");
        species.Text("routeSegment").ShouldBe("species");
        species.GetProperty("itemCount").GetInt32().ShouldBe(5);
    }

    /// <summary>
    /// The fixture holds one background file that parses as JSON but has no
    /// name, which is what a half-finished entry in the content repository
    /// looks like. It must be skipped, not crash the index and not be counted.
    /// </summary>
    [Fact]
    public async Task ContentTypes_SkipMalformedFilesWithoutFailing()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content-types"));

        var background = body.Array("types").Single(type => type.Text("key") == "background");

        background.GetProperty("itemCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task ContentTypes_AreCacheableAndValidated()
    {
        var response = await factory.CreateClient().GetAsync("/api/content-types");

        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task ContentTypes_ReturnNotModifiedForAMatchingETag()
    {
        var client = factory.CreateClient();
        var first = await client.GetAsync("/api/content-types");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/content-types");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        var second = await client.SendAsync(conditional);

        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await second.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ContentTypes_ReturnAFullBodyForAStaleETag()
    {
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/content-types");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"0000000000000000\""));

        var response = await factory.CreateClient().SendAsync(conditional);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

public sealed class EmptyCatalogueTests(EmptyContentApiFactory factory)
    : IClassFixture<EmptyContentApiFactory>
{
    /// <summary>
    /// A missing content directory is the normal state of a fresh clone. The
    /// API must come up and serve an empty catalogue, because the content is
    /// maintained in a separate repository on its own schedule.
    /// </summary>
    [Fact]
    public async Task MissingContentDirectory_StillServesTheRegistry()
    {
        var response = await factory.CreateClient().GetAsync("/api/content-types");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Against the registry rather than a literal: the exact list, in
        // order, is already pinned by ContentTypes_ListsEveryType, and what
        // this test is about is that an absent content directory costs no
        // types rather than how many there are.
        body.Array("types").Count().ShouldBe(ContentTypeRegistry.All.Count);
        body.Array("types").ShouldAllBe(type => type.GetProperty("itemCount").GetInt32() == 0);
    }

    [Fact]
    public async Task MissingContentDirectory_ListsAnEmptyPageRatherThanFailing()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Array("items").ShouldBeEmpty();
        body.GetProperty("page").GetProperty("totalItems").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task MissingContentDirectory_SearchesWithoutFailing()
    {
        var response = await factory.CreateClient().GetAsync("/api/search?q=wookiee");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("totalMatches").GetInt32().ShouldBe(0);
    }
}
