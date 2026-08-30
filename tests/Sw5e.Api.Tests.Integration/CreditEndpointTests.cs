using System.Net;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

/// <summary>
/// The attribution content types, served over the same endpoints as everything
/// else.
/// </summary>
/// <remarks>
/// These types exist so that credits are data an administrator can correct
/// rather than markup a developer edits, which means the API has to carry the
/// parts that make a credit worth anything: the specific contribution text,
/// and the separation between categories. Both are things a projection could
/// silently drop while every existing test stayed green — a credit reduced to
/// a bare name still lists, still pages and still resolves by key. So the
/// assertions below are on the content, not on the plumbing.
/// </remarks>
public sealed class CreditEndpointTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    [Theory]
    [InlineData("credit")]
    [InlineData("credit-category")]
    [InlineData("asset-credit")]
    public void EachAttributionTypeIsInTheRegistry(string key)
    {
        Sw5e.Domain.Content.ContentTypeRegistry.TryResolve(key, out var definition)
            .ShouldBeTrue($"'{key}' must resolve, or no store will ever be asked for it");
        definition!.Key.ShouldBe(key);
    }

    /// <summary>
    /// The route segments are plural, like every other type's, and resolve to
    /// the singular key the content directory is named after.
    /// </summary>
    [Theory]
    [InlineData("credits", "credit")]
    [InlineData("credit-categories", "credit-category")]
    [InlineData("asset-credits", "asset-credit")]
    public async Task AnAttributionTypeIsReachableByItsRouteSegment(
        string segment, string key)
    {
        var response = await factory.CreateClient().GetAsync($"/api/content/{segment}");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("type").ShouldBe(key);
    }

    /// <summary>
    /// The single most valuable field in the whole credits set: what this
    /// person actually did. It has to survive the projection into a list row,
    /// because the credits page reads rows rather than fetching every credit
    /// individually.
    /// </summary>
    /// <remarks>
    /// A summary is plain text — the projection strips markup so a snippet can
    /// be rendered anywhere without being parsed — so the emphasis around
    /// "epic" is gone from the row while the words are not. Both halves are
    /// asserted: the row must still carry the whole sentence, and the item
    /// body must still carry it exactly as the credit was written, because the
    /// body is what an editor loads to correct it.
    /// </remarks>
    [Fact]
    public async Task ACreditKeepsItsSpecificContributionInAListRow()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/credits");
        var body = await JsonResponse.ReadAsync(response);

        var karbacca = body
            .GetProperty("items")
            .EnumerateArray()
            .Single(item => item.Text("key") == "jedi-council-karbacca");

        karbacca.Text("name").ShouldBe("Karbacca");
        karbacca.Text("summary").ShouldBe("for the epic cover and SW5e logo");
    }

    /// <summary>
    /// The document itself is passed through verbatim, emphasis and all.
    /// </summary>
    [Fact]
    public async Task ACreditDocumentKeepsItsContributionExactlyAsWritten()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/credits/jedi-council-karbacca");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("data").Text("contribution")
            .ShouldBe("for the *epic* cover and SW5e logo");
        body.GetProperty("data").Text("categoryKey").ShouldBe("jedi-council");
    }

    /// <summary>
    /// Categories are the thing that must not collapse. A patron and a council
    /// member are owed different acknowledgements, so the category has to come
    /// back as a facet a caller can group by without parsing the key.
    /// </summary>
    [Fact]
    public async Task ACreditCarriesItsCategoryAsAFacet()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/credits");
        var items = (await JsonResponse.ReadAsync(response))
            .GetProperty("items")
            .EnumerateArray()
            .ToList();

        var categories = items
            .Select(item => item.GetProperty("facets").Text("categoryKey"))
            .ToList();

        categories.ShouldContain("jedi-council");
        categories.ShouldContain("patron");
        categories.Distinct().Count().ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// A name the archive damaged travels intact through JSON serialisation.
    /// Encoding damage is precisely the kind of thing that reappears at a
    /// transport boundary, and a mangled name is the worst defect this feature
    /// can ship.
    /// </summary>
    [Fact]
    public async Task ARepairedNameSurvivesTheWire()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/credits/patron-cesar-diaz");
        var raw = await response.Content.ReadAsStringAsync();
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("name").ShouldBe("César Díaz");
        body.GetProperty("data").Text("name").ShouldBe("César Díaz");
        raw.ShouldNotContain("�");
    }

    /// <summary>
    /// A cited picture arrives with everything needed to print the credit next
    /// to the image: who made it, which work it is, and why it may be shown.
    /// </summary>
    [Fact]
    public async Task ACitedAssetCarriesItsWholeCitation()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/asset-credits/brand-logo");
        var data = (await JsonResponse.ReadAsync(response)).GetProperty("data");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        data.Text("status").ShouldBe("cited");
        data.Text("artist").ShouldBe("Karbacca");
        data.Text("workTitle").ShouldBe("SW5e logo");
        data.Text("basis").ShouldBe("fan-content-policy");
        data.Text("provenance").ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// An inherited picture says so, and says nothing more. The site draws
    /// this as a labelled absence; if the API invented an artist here the site
    /// would print it beside the image as though it were true.
    /// </summary>
    [Fact]
    public async Task AnInheritedAssetReportsAnUnknownArtistRatherThanAGuess()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/asset-credits/species-wookiee");
        var data = (await JsonResponse.ReadAsync(response)).GetProperty("data");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        data.Text("status").ShouldBe("inherited-unattributed");
        data.Text("basis").ShouldBe("unrecorded");
        data.TryGetProperty("artist", out _).ShouldBeFalse();
        data.TryGetProperty("workTitle", out _).ShouldBeFalse();
        data.Text("provenance").ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Asking for every type at once is a legitimate search, and it stopped
    /// being possible the moment the cap was a hand-written number smaller
    /// than the registry.
    /// </summary>
    [Fact]
    public async Task SearchAcceptsAFilterNamingEveryRegisteredType()
    {
        var types = string.Join(
            ',',
            Sw5e.Domain.Content.ContentTypeRegistry.All.Select(definition => definition.Key));

        var response = await factory.CreateClient()
            .GetAsync($"/api/search?q=karbacca&types={Uri.EscapeDataString(types)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The catalogue lists the new types, because that is how any client
    /// discovers them.
    /// </summary>
    [Fact]
    public async Task TheContentTypeCatalogueListsTheAttributionTypes()
    {
        var response = await factory.CreateClient().GetAsync("/api/content-types");
        var keys = (await JsonResponse.ReadAsync(response))
            .GetProperty("types")
            .EnumerateArray()
            .Select(type => type.Text("key"))
            .ToList();

        keys.ShouldContain("credit");
        keys.ShouldContain("credit-category");
        keys.ShouldContain("asset-credit");
    }
}
