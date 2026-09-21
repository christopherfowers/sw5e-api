using System.Net;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

/// <summary>
/// The six starship types, served end to end.
/// </summary>
/// <remarks>
/// These are not "the same as species with a different word", which is what a
/// test that only checked a 200 would establish. Each assertion below is about
/// something the starship types do that no earlier type did: a route segment
/// containing hyphens has to survive the registry gate that exists to keep the
/// <c>{type}</c> value away from a path join; a rule chapter is titled rather
/// than named, so its display name comes from a different field; and the
/// list-row facets are per-type projections that are easy to add to the wrong
/// key and impossible to notice afterwards.
/// </remarks>
public sealed class StarshipContentEndpointTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    /// <summary>
    /// How many fixture documents each starship type holds. Declared rather
    /// than counted from disk, so a projection that silently dropped a document
    /// fails here instead of agreeing with itself.
    /// </summary>
    public static TheoryData<string, int> Types =>
        new()
        {
            { "starship-base-sizes", 1 },
            { "starship-deployments", 1 },
            { "starship-equipment", 4 },
            { "starship-modifications", 2 },
            { "starship-ventures", 2 },
            { "starship-rules", 1 },
        };

    [Theory]
    [MemberData(nameof(Types))]
    public async Task List_ServesEveryStarshipTypeByItsRouteSegment(string segment, int expected)
    {
        var response = await factory.CreateClient().GetAsync($"/api/content/{segment}");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("page").GetProperty("totalItems").GetInt32().ShouldBe(expected, segment);
        body.Array("items").ShouldAllBe(item => item.Text("name").Length > 0);
    }

    /// <summary>
    /// A hyphenated route segment must not be mistaken for a traversal attempt
    /// or resolved by prefix. <c>starship-equipment</c> is the interesting one:
    /// its key and its route segment are the same string, and the registry
    /// indexes both.
    /// </summary>
    [Theory]
    [InlineData("starship-equipment")]
    [InlineData("starship-modification")]
    [InlineData("starship-base-size")]
    public async Task List_AlsoAcceptsTheCanonicalKeyRatherThanTheRouteSegment(string key)
    {
        var response = await factory.CreateClient().GetAsync($"/api/content/{key}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("starship-equipments")]
    [InlineData("starship")]
    [InlineData("starship-")]
    public async Task List_RefusesANameThatOnlyLooksLikeAStarshipType(string segment)
    {
        var response = await factory.CreateClient().GetAsync($"/api/content/{segment}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Item_ServesAModificationWithItsGradeAndItsResolvedPrerequisite()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/starship-modifications/frame-mk-ii");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("name").ShouldBe("Frame, Mk II");
        body.Text("type").ShouldBe("starship-modification");

        var document = body.GetProperty("data");
        document.GetProperty("grade").GetInt32().ShouldBe(2);
        document.Text("modificationType").ShouldBe("universal");

        var prerequisite = document.Array("prerequisites").Single();
        prerequisite.Text("kind").ShouldBe("modification");
        prerequisite.Text("modificationName").ShouldBe("Frame, Mk I");
    }

    /// <summary>
    /// Ammunition is useless without a launcher, and the book only prints the
    /// link one way round, so the document has to carry it.
    /// </summary>
    [Fact]
    public async Task Item_ServesAmmunitionWithBothItsDamageScalesAndItsLauncher()
    {
        var body = await JsonResponse.ReadAsync(await factory.CreateClient()
            .GetAsync("/api/content/starship-equipment/proton-torpedo"));

        var document = body.GetProperty("data");

        document.Text("category").ShouldBe("ammunition");
        document.GetProperty("damage").GetProperty("numberOfDice").GetInt32().ShouldBe(2);
        document.GetProperty("damage").GetProperty("dieFaces").GetInt32().ShouldBe(10);
        document.GetProperty("damageForLargerShips").GetProperty("numberOfDice").GetInt32().ShouldBe(4);
        document.Array("firedBy").Select(launcher => launcher.GetString())
                .ShouldBe(["Torpedo launcher"]);
    }

    /// <summary>
    /// A base size's tier table is the ship's equivalent of a class table, and
    /// it is the whole reason the type is structured rather than prose.
    /// </summary>
    [Fact]
    public async Task Item_ServesABaseSizeWithItsTierTableAndItsRoles()
    {
        var body = await JsonResponse.ReadAsync(await factory.CreateClient()
            .GetAsync("/api/content/starship-base-sizes/small"));

        var document = body.GetProperty("data");

        document.GetProperty("modifications").GetProperty("baseModificationSlots")
                .GetInt32().ShouldBe(20);

        var tiers = document.GetProperty("tierProgression").Array("tiers").ToArray();
        tiers.Length.ShouldBe(6);
        tiers[0].GetProperty("tier").GetInt32().ShouldBe(0);
        tiers[5].GetProperty("armorClassBonus").GetInt32().ShouldBe(4);

        document.Array("roles").Count().ShouldBe(6);
        document.Array("roles").ShouldAllBe(role => role.Text("armor").Length > 0);
    }

    /// <summary>
    /// Rule chapters call their display name <c>title</c>, as sources do. A
    /// projection that assumed <c>name</c> would serve thirteen nameless rows.
    /// </summary>
    [Fact]
    public async Task Item_TakesARuleChaptersDisplayNameFromItsTitle()
    {
        var body = await JsonResponse.ReadAsync(await factory.CreateClient()
            .GetAsync("/api/content/starship-rules/combat"));

        body.Text("name").ShouldBe("Combat");
        body.GetProperty("data").GetProperty("chapterNumber").GetInt32().ShouldBe(9);
    }

    /// <summary>
    /// Facets are what a list row is rendered from, and each starship type
    /// projects a different set. Asserting the values rather than the keys is
    /// what makes this fail when a facet is declared against the wrong path.
    /// </summary>
    [Fact]
    public async Task List_ProjectsTheFacetsEachStarshipTypeIsScannedBy()
    {
        var client = factory.CreateClient();

        var modification = (await JsonResponse.ReadAsync(
                await client.GetAsync("/api/content/starship-modifications")))
            .Array("items").Single(item => item.Text("key") == "frame-mk-i");

        modification.GetProperty("facets").Text("grade").ShouldBe("1");
        modification.GetProperty("facets").Text("modificationType").ShouldBe("universal");

        var cannon = (await JsonResponse.ReadAsync(
                await client.GetAsync("/api/content/starship-equipment")))
            .Array("items").Single(item => item.Text("key") == "heavy-laser-cannon");

        cannon.GetProperty("facets").Text("category").ShouldBe("weapon");
        cannon.GetProperty("facets").Text("costInCredits").ShouldBe("4150");
        cannon.GetProperty("facets").Text("weapon.mounting").ShouldBe("primary");

        var chapter = (await JsonResponse.ReadAsync(
                await client.GetAsync("/api/content/starship-rules")))
            .Array("items").Single();

        chapter.GetProperty("facets").Text("chapterNumber").ShouldBe("9");
    }

    /// <summary>
    /// Search has to reach inside a starship document's prose, not just its
    /// name. "hardpoint" appears only in the body of the rule chapter and of a
    /// modification, which is exactly the case a name-only index would miss.
    /// </summary>
    [Fact]
    public async Task Search_ReachesTheProseOfAStarshipDocument()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/search?q=Constitution%20score"));

        var modifications = body.Array("groups")
            .Single(group => group.Text("type") == "starship-modification");

        modifications.Array("results").Select(result => result.GetProperty("item").Text("key"))
                     .OrderBy(key => key, StringComparer.Ordinal)
                     .ShouldBe(["frame-mk-i", "frame-mk-ii"]);

        modifications.Array("results").ShouldAllBe(result => result.Text("matchedIn") == "text");
    }

    /// <summary>
    /// The registry's route segments are what the site's navigation is built
    /// from, so they are served rather than guessed at by the client.
    /// </summary>
    [Fact]
    public async Task ContentTypes_NameEveryStarshipTypeAndItsCount()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content-types"));

        var equipment = body.Array("types")
            .Single(type => type.Text("key") == "starship-equipment");

        // The one type whose plural is not the singular with an "s", and the
        // one whose route segment is identical to its key.
        equipment.Text("name").ShouldBe("Starship Equipment");
        equipment.Text("pluralName").ShouldBe("Starship Equipment");
        equipment.Text("routeSegment").ShouldBe("starship-equipment");
        equipment.GetProperty("itemCount").GetInt32().ShouldBe(4);

        var ventures = body.Array("types")
            .Single(type => type.Text("key") == "starship-venture");

        ventures.Text("routeSegment").ShouldBe("starship-ventures");
        ventures.GetProperty("itemCount").GetInt32().ShouldBe(2);
    }
}
