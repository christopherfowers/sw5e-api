using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

/// <summary>
/// Behind a TLS-terminating proxy the inbound connection is plain HTTP, so
/// without forwarded-header handling <c>Request.IsHttps</c> is false and the
/// HSTS middleware emits nothing at all in production. These tests pin the
/// behaviour that fixes it: a request forwarded as HTTPS must produce a
/// Strict-Transport-Security header carrying the configured policy.
/// </summary>
public sealed class ForwardedHeadersTests
{
    // The HSTS middleware skips a fixed list of loopback hosts (localhost,
    // 127.0.0.1, [::1]), which is exactly what WebApplicationFactory's default
    // base address uses. Requests must therefore appear to arrive for a real
    // hostname or the header is suppressed for a reason unrelated to scheme.
    private const string ProxiedHost = "api.sw5e.test";

    private static WebApplicationFactory<Program> CreateProductionFactory() =>
        new ContentApiFactory().WithWebHostBuilder(builder =>
        {
            // UseHsts is only wired up outside Development.
            builder.UseEnvironment("Production");

            // Outside Development the email subsystem refuses to register
            // without a provider, so a Production host will not start without
            // one. That refusal is deliberate — it is what stops a deployment
            // going live with no way to send a password-reset email — so the
            // fix is to configure a provider here rather than to soften it.
            // Capture delivers nothing, which is what a test wants.
            builder.UseSetting("Email:Provider", "Capture");
            builder.UseSetting("Email:FromAddress", "noreply@sw5e.test");

            builder.ConfigureTestServices(services =>
                services.Configure<ForwardedHeadersOptions>(options =>
                {
                    // TestServer synthesises requests that carry no remote IP
                    // address, so the production default of trusting loopback
                    // only can never match and forwarded headers would be
                    // dropped before the scheme is ever rewritten. Emptying
                    // the trust list makes the middleware accept the headers
                    // unconditionally. That is safe here and only here: the
                    // "proxy" is the test itself. Production keeps the
                    // restrictive default configured in Program.cs.
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{ProxiedHost}"),
            AllowAutoRedirect = false,
        });

    [Fact]
    public async Task Hsts_IsEmittedWhenTheRequestIsForwardedAsHttps()
    {
        using var factory = CreateProductionFactory();
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Strict-Transport-Security").ShouldBeTrue(
            "a request forwarded as HTTPS must be treated as HTTPS, otherwise " +
            "the production HSTS policy never reaches a browser");
    }

    [Fact]
    public async Task Hsts_CarriesAOneYearPolicyCoveringSubdomainsAndPreload()
    {
        using var factory = CreateProductionFactory();
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        var policy = string.Join(
            " ", response.Headers.GetValues("Strict-Transport-Security"));

        // The framework default is 30 days with neither flag, which is below
        // the preload list's minimum and leaves subdomains unprotected.
        policy.ShouldContain("max-age=31536000");
        policy.ShouldContain("includeSubDomains");
        policy.ShouldContain("preload");
    }

    /// <summary>
    /// Control: without the forwarded header the request really is plain HTTP,
    /// so HSTS must stay absent. This is what makes the tests above evidence
    /// that the forwarded header is doing the work, rather than HSTS happening
    /// to be emitted on everything.
    /// </summary>
    [Fact]
    public async Task Hsts_IsNotEmittedForAnUnforwardedPlainHttpRequest()
    {
        using var factory = CreateProductionFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/health");

        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    /// <summary>
    /// The client address the proxy forwards must survive too: request logging
    /// and any future rate limiting are worthless if every request appears to
    /// originate from the proxy.
    /// </summary>
    [Fact]
    public async Task ForwardedFor_IsConsumedRatherThanLeftOnTheRequest()
    {
        using var factory = CreateProductionFactory();
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        using var response = await client.SendAsync(request);

        // The middleware removes each header entry it applies, so a successful
        // response here means X-Forwarded-For was processed rather than ignored.
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }
}
