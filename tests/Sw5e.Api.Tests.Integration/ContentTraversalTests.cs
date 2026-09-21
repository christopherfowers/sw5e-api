using System.Net;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

/// <summary>
/// The {type} and {key} route values are the only caller-controlled strings
/// anywhere near a path join, so these are the tests that matter most.
/// </summary>
/// <remarks>
/// Each traversal case is paired with the request it is trying to imitate, and
/// the fixture is arranged so the target genuinely exists: <c>source/phb</c> is
/// a real document reachable at its own route. A vulnerable implementation
/// would therefore answer the traversal with that document, and every
/// assertion below distinguishes that outcome from a refusal. Without the
/// paired positive case these tests would also pass against a repository that
/// simply held nothing.
/// </remarks>
public sealed class ContentTraversalTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    /// <summary>
    /// The document the traversal attempts below are aiming at. It is real, it
    /// is readable, and it lives in a sibling directory of the one being
    /// browsed.
    /// </summary>
    [Fact]
    public async Task TheTraversalTarget_IsGenuinelyReachableByItsOwnRoute()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/source/phb");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("data").Text("abbreviation").ShouldBe("PHB");
    }

    // Percent-encoded null bytes are absent from these tables on purpose: the
    // request line is rejected by the host's URL decoder before routing runs,
    // so a case built on one would assert on the platform rather than on
    // anything in this repository. Every vector below does reach the endpoint
    // as a single route value, which is confirmed by asserting on the Problem
    // Details title the endpoint itself produces.
    public static TheoryData<string> TraversalKeys() =>
    [
        "..%2Fsource%2Fphb",
        "..%2F..%2Fsource%2Fphb",
        "..%5Csource%5Cphb",
        "..%5C..%5Csource%5Cphb",
        "%2e%2e%2fsource%2fphb",
        "....%2F%2F..%2Fsource%2Fphb",
        "%2Fetc%2Fpasswd",
        "C%3A%5CWindows%5Cwin.ini",
        "wookiee.json",
        "..%2F..%2F..%2Fappsettings.json",
    ];

    /// <summary>
    /// A key that is not a slug is refused before the store is asked anything.
    /// The title assertion is what makes this a test of this repository rather
    /// than of ASP.NET Core: only the endpoint's own validation produces it, so
    /// the case cannot be satisfied by routing happening to miss.
    /// </summary>
    [Theory]
    [MemberData(nameof(TraversalKeys))]
    public async Task Item_RefusesATraversalInTheKey(string key)
    {
        var response = await factory.CreateClient().GetAsync($"/api/content/species/{key}");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonResponse.ReadAsync(response)).Text("title").ShouldBe("Invalid content key");

        // The traversal target's contents must not appear in the response by
        // any route, whatever status was used to refuse it.
        raw.ShouldNotContain("Player's Handbook");
        raw.ShouldNotContain("PHB");
        raw.ShouldNotContain("TestContent");
    }

    /// <summary>
    /// The leak itself, asserted on the body before the status, so the failure
    /// message names what escaped rather than only that a number was wrong.
    /// </summary>
    /// <remarks>
    /// A backslash is the vector that works here: the host leaves <c>%2F</c>
    /// encoded so an encoded forward slash never becomes a separator, but it
    /// decodes <c>%5C</c>, and on Windows a backslash is a directory separator
    /// once the value reaches Path.Combine. An implementation that joined this
    /// key to a path would answer with the source document from the sibling
    /// directory, at 200, through the species route.
    /// </remarks>
    [Fact]
    public async Task Item_TraversalDoesNotServeASiblingDocument()
    {
        var response = await factory.CreateClient()
            .GetAsync("/api/content/species/..%5Csource%5Cphb");

        var raw = await response.Content.ReadAsStringAsync();

        raw.ShouldNotContain("Player's Handbook");
        raw.ShouldNotContain("abbreviation");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    public static TheoryData<string> TraversalTypes() =>
    [
        "..%2F..%2Fsource",
        "..%5C..%5Csource",
        "%2e%2e%2f%2e%2e%2fsource",
        "source%2F..%2Fsource",
        "species.json",
    ];

    /// <summary>
    /// A type that is not in the registry is refused, so nothing built from it
    /// is ever joined to a path. The registry is a closed list of compile-time
    /// constants precisely so this cannot be reintroduced by a later edit.
    /// </summary>
    [Theory]
    [MemberData(nameof(TraversalTypes))]
    public async Task List_RefusesATraversalInTheType(string type)
    {
        var response = await factory.CreateClient().GetAsync($"/api/content/{type}");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await JsonResponse.ReadAsync(response)).Text("title").ShouldBe("Unknown content type");

        raw.ShouldNotContain("Player's Handbook");
        raw.ShouldNotContain("wookiee");
        raw.ShouldNotContain("TestContent");
    }

    /// <summary>
    /// Type resolution is case-insensitive, and it canonicalises. That is safe
    /// precisely because what comes back is the registry entry rather than the
    /// caller's spelling: whatever case was asked for, the lowercase constant
    /// is what any path or table name is built from.
    /// </summary>
    [Fact]
    public async Task List_CanonicalisesTheTypeItResolves()
    {
        var response = await factory.CreateClient().GetAsync("/api/content/SPECIES");
        var body = await JsonResponse.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("type").ShouldBe("species");
    }

    /// <summary>
    /// A bare dot-segment never reaches the endpoint: it fails to match the
    /// route template, so the host answers first. Asserted separately from the
    /// cases above, and asserted only for what is actually true of it, so that
    /// the endpoint's own guard is never credited with a refusal the framework
    /// made.
    /// </summary>
    [Theory]
    [InlineData("/api/content/species/..")]
    [InlineData("/api/content/..")]
    [InlineData("/api/content/%2e%2e")]
    public async Task DotSegments_AreRefusedBeforeRouting(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        raw.ShouldNotContain("wookiee");
        raw.ShouldNotContain("Player's Handbook");
    }

    /// <summary>
    /// Search takes its type filter from the same registry, so the same
    /// argument has to hold on that path too.
    /// </summary>
    [Theory]
    [InlineData("..%2F..%2Fsource")]
    [InlineData("species%2C..")]
    [InlineData("species%2Cstarships")]
    public async Task Search_RefusesAnUnknownTypeFilter(string types)
    {
        var response = await factory.CreateClient().GetAsync($"/api/search?q=wookiee&types={types}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Refusals must say what is allowed without saying anything about the
    /// server. A message that quoted the offending path back would turn a
    /// blocked traversal into a working directory-probe oracle.
    /// </summary>
    [Fact]
    public async Task Refusals_DiscloseNothingAboutTheServer()
    {
        var client = factory.CreateClient();

        foreach (var url in new[]
                 {
                     "/api/content/species/..%2F..%2Fsource%2Fphb",
                     "/api/content/..%2F..%2Fsource",
                     "/api/content/species?sort=..%2F..%2Fetc",
                 })
        {
            var raw = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

            raw.ShouldNotContain("TestContent");
            raw.ShouldNotContain("C:\\");
            raw.ShouldNotContain("/home/");
            raw.ShouldNotContain("Sw5e.Infrastructure");
            raw.ShouldNotContain("System.");
            raw.ShouldNotContain("StackTrace");
        }
    }

    /// <summary>
    /// The security headers are emitted by middleware that runs ahead of
    /// routing, so a refusal must carry them too. A blocked request that
    /// answers without a Content-Security-Policy is still a response an
    /// attacker can work with.
    /// </summary>
    [Theory]
    [InlineData("/api/content/species/..%2F..%2Fsource%2Fphb")]
    [InlineData("/api/content/..%2F..%2Fsource")]
    [InlineData("/api/content/starships")]
    [InlineData("/api/content/species?pageSize=9999")]
    [InlineData("/api/search")]
    public async Task Refusals_StillCarryTheSecurityHeaders(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);

        foreach (var header in new[]
                 {
                     "Content-Security-Policy",
                     "X-Content-Type-Options",
                     "Referrer-Policy",
                     "Permissions-Policy",
                     "Cross-Origin-Opener-Policy",
                     "Cross-Origin-Resource-Policy",
                 })
        {
            response.Headers.Contains(header).ShouldBeTrue(
                $"a refused request must still carry {header}");
        }

        string.Join(" ", response.Headers.GetValues("Content-Security-Policy"))
            .ShouldContain("default-src 'none'");
    }

    [Theory]
    [InlineData("/api/content-types")]
    [InlineData("/api/content/species")]
    [InlineData("/api/content/species/wookiee")]
    [InlineData("/api/search?q=wookiee")]
    public async Task SuccessfulContentResponses_CarryTheSecurityHeaders(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
        response.Headers.Contains("X-Content-Type-Options").ShouldBeTrue();
        response.Headers.Contains("Cross-Origin-Resource-Policy").ShouldBeTrue();
    }
}
