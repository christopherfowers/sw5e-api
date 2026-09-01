using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Sw5e.Api.Features.Site;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

/// <summary>
/// The endpoint the QA banner is drawn from, and the default that keeps that
/// banner off the live site.
/// </summary>
/// <remarks>
/// <para>
/// The asymmetry these tests protect is the whole design. A missing banner in
/// QA is an inconvenience somebody fixes by setting a variable. A
/// "test environment, nothing here is kept" banner on the live site tells every
/// reader that the reference they are using is disposable, and it would be
/// served from every prerendered page until somebody noticed. So the default
/// has to be production, and the default has to be tested — a default nobody
/// asserts is a default that survives exactly as long as nobody edits the line
/// it lives on.
/// </para>
/// <para>
/// <see cref="UnconfiguredIsProduction"/> is the test that matters. It is the
/// one case a hosted test cannot reach — both hosts substitute a name when none
/// is given, which is the behaviour under test — so it goes at the decision
/// directly, with the values a deployment where somebody forgot actually
/// produces.
/// </para>
/// </remarks>
public sealed class SiteEnvironmentEndpointTests
{
    private const string Path = "/api/site/environment";

    /// <summary>
    /// Hosts the API in one named environment, or in none at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> hosts in Development
    /// unless it is told otherwise, which is right for every other test in this
    /// project and useless here: it would mean the "unconfigured" case was
    /// silently configured.
    /// </para>
    /// <para>
    /// It cannot reproduce a missing environment name, and that is why
    /// <see cref="UnconfiguredIsProduction"/> below goes at
    /// <c>SiteEnvironmentEndpoint.Describe</c> directly instead. Both the test
    /// host and the real host substitute a name of their own when none is
    /// given — which is the very behaviour under test — so asking this factory
    /// for "no environment" would only ever exercise whatever it substituted.
    /// </para>
    /// </remarks>
    private sealed class EnvironmentFactory(string environmentName) : ContentApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // "environment" is the host setting ASPNETCORE_ENVIRONMENT maps onto.
            builder.UseSetting(WebHostDefaults.EnvironmentKey, environmentName);

            // Only Development skips HTTPS redirection in Program.cs, so every
            // other case here would be answered with a 307 to https before the
            // endpoint ran. The client is told the request already arrived over
            // TLS, which is what the reverse proxy in front of every deployed
            // environment reports and what UseForwardedHeaders honours.
            builder.UseSetting("ForwardedHeaders:KnownProxies:0", "127.0.0.1");

            // Outside Development the email registration refuses to build
            // without a provider, deliberately: an API that starts happily and
            // then silently drops every password-reset mail is worse than one
            // that will not start. Nothing here sends mail, so the capture
            // provider — which writes to the log and goes nowhere — satisfies
            // that check without weakening it.
            builder.UseSetting("Email:Provider", "Capture");
            builder.UseSetting("Email:FromAddress", "noreply@sw5e.test");
        }

        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
    }

    private static async Task<(HttpStatusCode Status, string Name, bool IsProduction)>
        AskAsync(string environmentName)
    {
        using var factory = new EnvironmentFactory(environmentName);

        var response = await factory.Client().GetAsync(Path);
        var body = await JsonResponse.ReadAsync(response);

        return (
            response.StatusCode,
            body.Text("name"),
            body.GetProperty("isProduction").GetBoolean());
    }

    /// <summary>
    /// The one that must never be deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value here stands for a deployment nobody told which environment it
    /// is. This is not a formality: the framework's own <c>IsProduction()</c>
    /// answers false for a blank name, so an endpoint built on that predicate
    /// alone reports the live site as a test environment the moment a compose
    /// file carries a bare <c>ASPNETCORE_ENVIRONMENT=</c> or a deploy template
    /// renders an absent field. This assertion failed for exactly that reason
    /// before the normalisation in <c>SiteEnvironmentEndpoint.Describe</c> was
    /// written, and it will fail again the day somebody removes it.
    /// </para>
    /// <para>
    /// Flip the default in <c>Describe</c> — return <c>false</c> for an unknown
    /// name, or drop the blank check and defer to <c>IsProduction()</c> — and
    /// this is what goes red.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnconfiguredIsProduction(string? environmentName)
    {
        var answer = SiteEnvironmentEndpoint.Describe(environmentName);

        answer.IsProduction.ShouldBeTrue(
            "a deployment that has not been told which environment it is must report " +
            "production, or forgetting one variable puts a test-environment banner on the " +
            "live site");
        answer.Name.ShouldBe(Environments.Production);
    }

    /// <summary>
    /// And the same answer through the whole stack, for the case the test host
    /// can reproduce: an environment explicitly named Production.
    /// </summary>
    [Fact]
    public async Task ProductionReportsProduction()
    {
        var (status, name, isProduction) = await AskAsync(Environments.Production);

        status.ShouldBe(HttpStatusCode.OK);
        name.ShouldBe(Environments.Production);
        isProduction.ShouldBeTrue();
    }

    [Theory]
    [InlineData("QA")]
    [InlineData("Staging")]
    [InlineData("Development")]
    // Not on any list of known environments. Anything that is not production is
    // not production, so a deployment nobody thought to name still says so.
    [InlineData("Preview")]
    public async Task AnythingOtherThanProductionReportsItself(string environmentName)
    {
        var (status, name, isProduction) = await AskAsync(environmentName);

        status.ShouldBe(HttpStatusCode.OK);
        name.ShouldBe(environmentName);
        isProduction.ShouldBeFalse();
    }

    /// <summary>
    /// Anonymous, because almost nobody using QA is signed in, and a banner that
    /// only appeared after sign-in would be missing from the pages most likely
    /// to be mistaken for the live site.
    /// </summary>
    [Fact]
    public async Task EnvironmentIsReadableWithoutASession()
    {
        var (status, _, _) = await AskAsync("QA");

        status.ShouldNotBe(HttpStatusCode.Unauthorized);
        status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A cached answer is an answer from whichever environment filled the cache
    /// first, which for this endpoint is the one failure mode worth ruling out
    /// outright.
    /// </summary>
    [Fact]
    public async Task EnvironmentIsNeverCached()
    {
        using var factory = new EnvironmentFactory("QA");

        var response = await factory.Client().GetAsync(Path);

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }
}
