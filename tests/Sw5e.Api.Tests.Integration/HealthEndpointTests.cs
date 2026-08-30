using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class HealthEndpointTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    [Theory]
    // The container image probes the first directly. The second is where the
    // probe lands from outside, because the QA reverse proxy routes /api/* here
    // without stripping the prefix — it used to answer 404.
    [InlineData("/health")]
    [InlineData("/api/health")]
    public async Task Health_ReturnsOk(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ReportsHealthyStatus()
    {
        var body = await factory.CreateClient()
            .GetFromJsonAsync<HealthResponse>("/health");

        body!.Status.ShouldBe("healthy");
    }

    private sealed record HealthResponse(string Status);
}
