using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await factory.CreateClient().GetAsync("/health");

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
