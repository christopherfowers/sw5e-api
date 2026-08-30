using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Sw5e.Domain.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The whole API, hosted over PostgreSQL, answering the routes the site calls.
/// </summary>
/// <remarks>
/// <para>
/// The repository tests establish that the store behaves; these establish that
/// the store is actually the one the application uses when it is configured to
/// be, and that everything between the store and the wire still works — the
/// registry endpoint, paging, ETags, the item body passing through untouched.
/// A configuration switch that quietly fell back to the file-backed store would
/// pass every other test in this project.
/// </para>
/// <para>
/// The fixture directory is deliberately <em>not</em> mounted: <c>Content:RootPath</c>
/// is pointed at a directory that does not exist. If any endpoint answered from
/// the filesystem after all, it would answer with an empty catalogue and every
/// count below would be zero.
/// </para>
/// </remarks>
public sealed class DatabaseContentApiTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "api_tests";

    private DatabaseBackedApi CreateApi() => new(Database.ConnectionString);

    [DockerFact]
    public async Task ContentTypes_AreServedWithTheCountsFromTheDatabase()
    {
        using var api = CreateApi();

        var response = await api.CreateClient().GetAsync("/api/content-types");
        var body = await ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var types = body.GetProperty("types").EnumerateArray().ToArray();

        types.Length.ShouldBe(ContentTypeRegistry.All.Count);

        foreach (var (key, expected) in ContentFixture.ExpectedCounts)
        {
            types.Single(type => Text(type, "key") == key)
                 .GetProperty("itemCount").GetInt32()
                 .ShouldBe(expected, key);
        }
    }

    [DockerFact]
    public async Task List_IsPagedFilteredAndOrderedByTheDatabase()
    {
        using var api = CreateApi();
        var client = api.CreateClient();

        var all = await ReadAsync(await client.GetAsync("/api/content/species"));

        all.GetProperty("items").EnumerateArray().Select(item => Text(item, "name"))
           .ShouldBe(["Human", "Twi'lek", "Wookiee", "Zabrak"]);

        var filtered = await ReadAsync(await client.GetAsync("/api/content/species?name=WOOK"));

        filtered.GetProperty("items").EnumerateArray().Select(item => Text(item, "key"))
                .ShouldBe(["wookiee"]);

        var page = await ReadAsync(await client.GetAsync("/api/content/species?page=2&pageSize=2"));

        page.GetProperty("items").EnumerateArray().Select(item => Text(item, "key"))
            .ShouldBe(["wookiee", "zabrak"]);

        var info = page.GetProperty("page");
        info.GetProperty("totalItems").GetInt32().ShouldBe(4);
        info.GetProperty("totalPages").GetInt32().ShouldBe(2);
    }

    [DockerFact]
    public async Task List_RowsCarryTheProjectedFields()
    {
        using var api = CreateApi();

        var body = await ReadAsync(await api.CreateClient().GetAsync("/api/content/power"));

        var push = body.GetProperty("items").EnumerateArray()
                       .Single(item => Text(item, "key") == "force-push");

        Text(push, "name").ShouldBe("Force Push");
        Text(push, "sourceKey").ShouldBe("phb");
        Text(push, "contentSet").ShouldBe("core");
        Text(push, "summary").ShouldContain("telekinetic");
        Text(push.GetProperty("facets"), "powerType").ShouldBe("force");
        Text(push.GetProperty("facets"), "level").ShouldBe("1");
    }

    /// <summary>
    /// The item endpoint returns the document as its JSON Schema defines it.
    /// </summary>
    [DockerFact]
    public async Task Item_IsServedWithItsWholeNestedDocument()
    {
        using var api = CreateApi();

        var response = await api.CreateClient().GetAsync("/api/content/monster/womp-rat");
        var body = await ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Text(body, "type").ShouldBe("monster");
        Text(body, "key").ShouldBe("womp-rat");
        Text(body, "name").ShouldBe("Womp rat");

        var data = body.GetProperty("data");

        data.GetProperty("challengeRating").GetString().ShouldBe("1/8");
        data.GetProperty("armor").GetProperty("class").GetInt32().ShouldBe(12);
        data.GetProperty("abilities").GetProperty("wisdom").GetProperty("score").GetInt32().ShouldBe(10);
        data.GetProperty("behaviors").EnumerateArray().Single()
            .GetProperty("name").GetString().ShouldBe("Bite");
    }

    [DockerFact]
    public async Task Item_ThatDoesNotExistIsANotFoundProblem()
    {
        using var api = CreateApi();

        var response = await api.CreateClient().GetAsync("/api/content/species/rancor");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    /// <summary>
    /// A traversal in the key is refused, and nothing about the database or the
    /// server comes back with the refusal.
    /// </summary>
    [DockerTheory]
    [InlineData("..%2Fsource%2Fphb")]
    [InlineData("..%5Csource%5Cphb")]
    [InlineData("wookiee%27%20OR%20%271%27%3D%271")]
    public async Task Item_RefusesAKeyThatIsNotASlug(string key)
    {
        using var api = CreateApi();

        var response = await api.CreateClient().GetAsync($"/api/content/species/{key}");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Text(await ReadAsync(response), "title").ShouldBe("Invalid content key");

        // The traversal target is a real document reachable at its own route,
        // so a store that resolved the key would have leaked it.
        raw.ShouldNotContain("Player's Handbook");
        raw.ShouldNotContain("content_item");
        raw.ShouldNotContain("Npgsql");
        raw.ShouldNotContain("StackTrace");
    }

    [DockerFact]
    public async Task Search_IsGroupedRankedAndExplainedByTheDatabase()
    {
        using var api = CreateApi();

        var body = await ReadAsync(await api.CreateClient().GetAsync("/api/search?q=outlast"));

        var species = body.GetProperty("groups").EnumerateArray()
                          .Single(group => Text(group, "type") == "species");

        var hit = species.GetProperty("results").EnumerateArray()
                         .Single(result => Text(result.GetProperty("item"), "key") == "wookiee");

        Text(hit, "matchedIn").ShouldBe("text");
        Text(hit, "snippet").ShouldContain("outlast");
        Text(hit, "snippet").ShouldContain("life debts");
    }

    [DockerFact]
    public async Task Responses_AreCacheableAndValidated()
    {
        using var api = CreateApi();
        var client = api.CreateClient();

        var first = await client.GetAsync("/api/content/species?page=1&pageSize=2");

        first.Headers.ETag.ShouldNotBeNull();

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get, "/api/content/species?page=1&pageSize=2");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        (await client.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.NotModified);

        var second = await client.GetAsync("/api/content/species?page=2&pageSize=2");
        second.Headers.ETag!.Tag.ShouldNotBe(first.Headers.ETag!.Tag);
    }

    /// <summary>
    /// Liveness stays a statement about the process, and readiness becomes a
    /// statement about the database.
    /// </summary>
    [DockerFact]
    public async Task Health_ReportsLivenessAndReadinessSeparately()
    {
        using var api = CreateApi();
        var client = api.CreateClient();

        var live = await client.GetAsync("/health");
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        Text(await ReadAsync(live), "status").ShouldBe("healthy");

        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);

        var report = await ReadAsync(ready);
        Text(report, "status").ShouldBe("healthy");

        var database = report.GetProperty("checks").EnumerateArray()
                             .Single(check => Text(check, "name") == "database");

        Text(database, "status").ShouldBe("healthy");
    }

    /// <summary>
    /// Readiness fails while the database is unreachable, and liveness does not.
    /// </summary>
    /// <remarks>
    /// The pair is the point. If liveness also failed, an orchestrator would
    /// restart every API container during a database outage, destroying the
    /// capacity that was still serving cached and static responses and doing
    /// nothing whatsoever for the database.
    /// </remarks>
    [DockerFact]
    public async Task Health_ReadinessFailsWhileLivenessHoldsWhenTheDatabaseIsDown()
    {
        var unreachable = new Npgsql.NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            Port = 65_433,
            Timeout = 2,
        };

        using var api = new DatabaseBackedApi(unreachable.ConnectionString);
        var client = api.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var ready = await client.GetAsync("/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var raw = await ready.Content.ReadAsStringAsync();
        raw.ShouldNotContain("65433");
        raw.ShouldNotContain("Password");
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string Text(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? string.Empty;
}

/// <summary>
/// Hosts the real API with its content store pointed at PostgreSQL.
/// </summary>
public sealed class DatabaseBackedApi(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Content:Store", "database");
        builder.UseSetting("ConnectionStrings:Sw5e", connectionString);

        // Pointed at nothing on purpose. Every count these tests assert would
        // still be produced by a file-backed store reading the fixture, so the
        // filesystem is taken away to make sure the answers can only have come
        // from the database.
        builder.UseSetting(
            "Content:RootPath",
            Path.Combine(AppContext.BaseDirectory, "TestContent-not-mounted"));

        builder.UseSetting("Sw5e:Database:MaxRetryCount", "0");
    }
}
