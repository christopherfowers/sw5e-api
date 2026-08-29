using Shouldly;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class SecurityHeadersTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("Content-Security-Policy")]
    [InlineData("X-Content-Type-Options")]
    [InlineData("Referrer-Policy")]
    [InlineData("Permissions-Policy")]
    [InlineData("Cross-Origin-Opener-Policy")]
    public async Task Response_IncludesSecurityHeader(string headerName)
    {
        var response = await factory.CreateClient().GetAsync("/health");

        response.Headers.Contains(headerName).ShouldBeTrue(
            $"every response must carry the {headerName} header");
    }

    [Fact]
    public async Task ContentSecurityPolicy_ForbidsInlineScript()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        var policy = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));

        policy.ShouldNotContain("unsafe-inline");
        policy.ShouldNotContain("unsafe-eval");
        policy.ShouldContain("default-src 'none'");
        policy.ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Response_DoesNotLeakServerBanner()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        response.Headers.Contains("Server").ShouldBeFalse();
        response.Headers.Contains("X-Powered-By").ShouldBeFalse();
    }
}
