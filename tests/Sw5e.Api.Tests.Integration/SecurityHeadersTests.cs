using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

public sealed class SecurityHeadersTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// Every header SecurityHeadersMiddleware emits. Adding one to the
    /// middleware without adding it here leaves it permanently unasserted,
    /// which is how Cross-Origin-Resource-Policy went uncovered.
    /// </summary>
    public static TheoryData<string> SecurityHeaderNames() =>
    [
        "Content-Security-Policy",
        "X-Content-Type-Options",
        "Referrer-Policy",
        "Permissions-Policy",
        "Cross-Origin-Opener-Policy",
        "Cross-Origin-Resource-Policy",
    ];

    [Theory]
    [MemberData(nameof(SecurityHeaderNames))]
    public async Task Response_IncludesSecurityHeader(string headerName)
    {
        var response = await factory.CreateClient().GetAsync("/health");

        response.Headers.Contains(headerName).ShouldBeTrue(
            $"every response must carry the {headerName} header");
    }

    /// <summary>
    /// SecurityHeadersMiddleware documents itself as running first so that
    /// error responses are covered too, but nothing verified that claim: every
    /// other assertion here hits a route that exists. A 404 never reaches an
    /// endpoint, so this fails if the middleware is ever reordered behind
    /// routing or reimplemented as something endpoint-scoped.
    /// </summary>
    [Theory]
    [MemberData(nameof(SecurityHeaderNames))]
    public async Task ErrorResponse_IncludesSecurityHeader(string headerName)
    {
        var response = await factory.CreateClient().GetAsync("/no-such-route");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.Contains(headerName).ShouldBeTrue(
            $"error responses must carry the {headerName} header too");
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

    // WebApplicationFactory<T>.CreateClient() always routes requests through
    // the in-memory TestServer handler, never over a real socket, so it can
    // never observe headers (or their absence) that only a genuine Kestrel
    // listener would add. This test spins up the app on a real Kestrel
    // socket on an ephemeral loopback port and hits it with a plain
    // HttpClient, so it actually fails if ConfigureKestrel(AddServerHeader =
    // false) in Program.cs is ever removed.
    [Fact]
    public async Task Response_DoesNotLeakServerBanner()
    {
        await using var kestrelFactory = new KestrelWebApplicationFactory();
        _ = kestrelFactory.Server; // forces CreateHost to run and populate ServerAddress
        using var client = new HttpClient { BaseAddress = kestrelFactory.ServerAddress };

        var response = await client.GetAsync("/health");

        response.Headers.Contains("Server").ShouldBeFalse();
        response.Headers.Contains("X-Powered-By").ShouldBeFalse();
    }

    /// <summary>
    /// Hosts the app on a real Kestrel listener bound to an ephemeral loopback
    /// port instead of the in-memory TestServer, so headers Kestrel itself
    /// would add or suppress are actually observable over the wire.
    /// </summary>
    private sealed class KestrelWebApplicationFactory : WebApplicationFactory<Program>
    {
        public Uri ServerAddress { get; private set; } = null!;

        // The second, real Kestrel host built in CreateHost. Only the
        // TestServer-based host returned from CreateHost is disposed by the
        // base class, so this one must be stopped and disposed explicitly or
        // its listening socket leaks for the rest of the test process.
        private IHost? _kestrelHost;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Build the in-memory TestServer host first; it's what this
            // factory must return so the rest of WebApplicationFactory's
            // plumbing (Server, Services, etc.) keeps working.
            var testHost = builder.Build();

            // Reconfigure the same builder to use Kestrel on an ephemeral
            // loopback port and build a second, real host from it.
            builder.ConfigureWebHost(webHostBuilder => webHostBuilder
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0"));

            _kestrelHost = builder.Build();
            _kestrelHost.Start();

            var addresses = _kestrelHost.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();

            ServerAddress = addresses!.Addresses
                .Select(address => new Uri(address))
                .Last();

            testHost.Start();
            return testHost;
        }

        public override async ValueTask DisposeAsync()
        {
            if (_kestrelHost is not null)
            {
                await _kestrelHost.StopAsync();
                _kestrelHost.Dispose();
                _kestrelHost = null;
            }

            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _kestrelHost is not null)
            {
                _kestrelHost.StopAsync().GetAwaiter().GetResult();
                _kestrelHost.Dispose();
                _kestrelHost = null;
            }

            base.Dispose(disposing);
        }
    }
}
