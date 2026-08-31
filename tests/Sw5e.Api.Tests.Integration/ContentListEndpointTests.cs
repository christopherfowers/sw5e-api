using System.Net;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class ContentListEndpointTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    [Fact]
    public async Task List_ReturnsThePageAndTheTotal()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("type").ShouldBe("species");
        body.Array("items").Count().ShouldBe(5);

        var page = body.GetProperty("page");
        page.GetProperty("number").GetInt32().ShouldBe(1);
        page.GetProperty("size").GetInt32().ShouldBe(25);
        page.GetProperty("totalItems").GetInt32().ShouldBe(5);
        page.GetProperty("totalPages").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task List_RowsCarryTheFieldsATableNeeds()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/power"));

        var push = body.Array("items").Single(item => item.Text("key") == "force-push");

        push.Text("name").ShouldBe("Force Push");
        push.Text("sourceKey").ShouldBe("phb");
        push.Text("contentSet").ShouldBe("core");
        push.Text("summary").ShouldContain("telekinetic");
        push.GetProperty("facets").Text("powerType").ShouldBe("force");
        push.GetProperty("facets").Text("level").ShouldBe("1");
    }

    [Fact]
    public async Task List_OrdersByNameAscendingByDefault()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/species"));

        body.Array("items").Select(item => item.Text("name"))
            .ShouldBe(["Bothan", "Human", "Twi'lek", "Wookiee", "Zabrak"]);
    }

    [Fact]
    public async Task List_HonoursSortDirection()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/species?sort=name&direction=desc"));

        body.Array("items").First().Text("name").ShouldBe("Zabrak");
    }

    [Fact]
    public async Task List_FiltersOnNameCaseInsensitively()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/species?name=WOOK"));

        body.Array("items").Select(item => item.Text("key")).ShouldBe(["wookiee"]);
        body.GetProperty("page").GetProperty("totalItems").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task List_FiltersOnContentSet()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/species?contentSet=expanded-content"));

        body.Array("items").Select(item => item.Text("key")).ShouldBe(["zabrak"]);
    }

    /// <summary>
    /// The total must describe the filtered set, not the page. Reporting the
    /// page length instead is the classic pagination bug, and it makes the UI
    /// show one page when there are three.
    /// </summary>
    [Fact]
    public async Task List_ReportsTheTotalOfTheFilteredSetNotOfThePage()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/species?page=2&pageSize=2"));

        body.Array("items").Count().ShouldBe(2);

        var page = body.GetProperty("page");
        page.GetProperty("totalItems").GetInt32().ShouldBe(5);
        page.GetProperty("totalPages").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task List_LastPageHoldsTheRemainder()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/species?page=3&pageSize=2"));

        body.Array("items").Select(item => item.Text("name")).ShouldBe(["Zabrak"]);
    }

    [Fact]
    public async Task List_PageBeyondTheEndIsEmptyRatherThanAnError()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species?page=99&pageSize=2");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Array("items").ShouldBeEmpty();
        body.GetProperty("page").GetProperty("totalItems").GetInt32().ShouldBe(5);
    }

    /// <summary>
    /// Paging must be a total order. Without a tiebreaker two pages can overlap
    /// or skip, which is invisible in a five-item fixture unless the union is
    /// checked against the whole set.
    /// </summary>
    [Fact]
    public async Task List_PagesPartitionTheSetWithoutOverlapOrGap()
    {
        var client = factory.CreateClient();
        var seen = new List<string>();

        for (var page = 1; page <= 3; page++)
        {
            var body = await JsonResponse.ReadAsync(
                await client.GetAsync($"/api/content/species?page={page}&pageSize=2"));

            seen.AddRange(body.Array("items").Select(item => item.Text("key")));
        }

        seen.ShouldBe(["bothan", "human", "twilek", "wookiee", "zabrak"], ignoreOrder: true);
        seen.Distinct().Count().ShouldBe(5);
    }

    [Fact]
    public async Task List_AcceptsTheLargestPermittedPageSize()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species?pageSize=100");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/api/content/species?pageSize=101")]
    [InlineData("/api/content/species?pageSize=0")]
    [InlineData("/api/content/species?pageSize=-1")]
    [InlineData("/api/content/species?page=0")]
    [InlineData("/api/content/species?page=-3")]
    public async Task List_RejectsOutOfRangePaging(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    /// <summary>
    /// An unknown sort field is rejected rather than ignored. Silently falling
    /// back to the default is how an ORDER BY built from caller input gets
    /// introduced later without anyone noticing the field was never validated.
    /// </summary>
    [Theory]
    [InlineData("notAField")]
    [InlineData("name; drop table content")]
    [InlineData("Name")]
    [InlineData("level")]
    [InlineData("sourcekey")]
    public async Task List_RejectsUnknownSortFields(string sort)
    {
        var response = await factory.CreateClient()
            .GetAsync($"/api/content/species?sort={Uri.EscapeDataString(sort)}");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.Text("title").ShouldBe("Unknown sort field");
    }

    /// <summary>
    /// The rejected value must not be echoed. Reflecting caller input into a
    /// response body is how a validation message becomes a delivery mechanism
    /// for whatever eventually renders it.
    /// </summary>
    [Fact]
    public async Task List_DoesNotEchoARejectedSortField()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/species?sort=" + Uri.EscapeDataString("<script>alert(1)</script>"));
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        raw.ShouldNotContain("script");
        raw.ShouldNotContain("alert");
    }

    [Fact]
    public async Task List_RejectsAnUnknownSortDirection()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/species?direction=sideways");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_RejectsAnOversizedNameFilter()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/species?name=" + new string('a', 101));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_RejectsAnUnknownType()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/starships");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.Text("title").ShouldBe("Unknown content type");
    }

    [Fact]
    public async Task List_AcceptsTheRouteSegmentAsWellAsTheKey()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/powers");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Canonicalised back to the key, so a client never has to reconcile two
        // spellings of the same type.
        body.Text("type").ShouldBe("power");
    }

    /// <summary>
    /// Maneuvers reach the store through the plural the site links to, and the
    /// row a list page renders carries the two things a reader scans a maneuver
    /// list for: which list it is on, and what it costs.
    /// </summary>
    [Fact]
    public async Task List_ServesManeuversThroughThePluralTheSiteLinksTo()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/maneuvers");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("type").ShouldBe("maneuver");
        body.Array("items").Count().ShouldBe(3);

        var riposte = body.Array("items").Single(item => item.Text("key") == "riposte-improved");

        riposte.Text("name").ShouldBe("Riposte (Improved)");
        riposte.Text("summary").ShouldContain("superiority die");
        riposte.GetProperty("facets").Text("maneuverType").ShouldBe("physical");
        riposte.GetProperty("facets").Text("superiorityDice").ShouldBe("1");
        riposte.GetProperty("facets").Text("improves").ShouldBe("Riposte");
    }

    /// <summary>
    /// A lightsaber form has no top-level prose at all — its rules text is
    /// split into the effect that fires on adoption and the one that holds
    /// while the form is worn — so its summary has to be read out of that
    /// array. A row with no summary is what a broken projection looks like.
    /// </summary>
    [Fact]
    public async Task List_SummarisesALightsaberFormFromItsFirstEffect()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/lightsaber-forms"));

        var form = body.Array("items").Single(item => item.Text("key") == "shii-cho-form");

        form.Text("name").ShouldBe("Shii-Cho Form");
        form.Text("summary").ShouldContain("bonus action to adopt this form");
    }

    /// <summary>
    /// A weapon focus is filtered by the group of weapons it applies to, which
    /// is the only thing separating one of the eight from another once the
    /// prose is stripped.
    /// </summary>
    [Fact]
    public async Task List_FacetsAWeaponFocusByItsWeaponGroup()
    {
        var body = await JsonResponse.ReadAsync(
            await factory.CreateClient().GetAsync("/api/content/weapon-focuses"));

        var focus = body.Array("items").Single(item => item.Text("key") == "blade-focus");

        focus.GetProperty("facets").Text("weaponGroup").ShouldBe("blade");
        focus.Text("sourceKey").ShouldBe("wh");
    }

    [Fact]
    public async Task List_IsCacheableAndValidated()
    {
        var client = factory.CreateClient();
        var first = await client.GetAsync("/api/content/species?page=1&pageSize=2");

        first.Headers.ETag.ShouldNotBeNull();

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get, "/api/content/species?page=1&pageSize=2");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        (await client.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    /// <summary>
    /// Two different pages must not share a validator, or a client that has
    /// page one cached is served a 304 when it asks for page two.
    /// </summary>
    [Fact]
    public async Task List_ETagDistinguishesPages()
    {
        var client = factory.CreateClient();

        var first = await client.GetAsync("/api/content/species?page=1&pageSize=2");
        var second = await client.GetAsync("/api/content/species?page=2&pageSize=2");

        first.Headers.ETag!.Tag.ShouldNotBe(second.Headers.ETag!.Tag);
    }
}
