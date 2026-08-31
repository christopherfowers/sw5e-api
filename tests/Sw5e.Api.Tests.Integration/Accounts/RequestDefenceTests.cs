using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// The defences that apply to every account request regardless of who is making
/// it: cross-site forgery, content type, and the attempt budget.
/// </summary>
[Collection(AccountTestCollection.Name)]
public sealed class RequestDefenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AStateChangingRequestWithNoOriginIsRefused()
    {
        var client = _factory.CreateOriginlessClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = AccountFlow.NewAddress("no-origin"), displayName = "Nobody" });

        // Fails closed. Every browser sends Origin on an unsafe request, so a
        // request without one is not the browser application this API serves.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _factory.Email.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStateChangingRequestFromAnotherSiteIsRefused()
    {
        var client = _factory.CreateOriginlessClient();
        client.DefaultRequestHeaders.Add("Origin", "https://sw5e-phishing.example");

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = AccountFlow.NewAddress("evil-origin"), displayName = "Nobody" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The check has to happen before the handler, not after it. If a
        // registration had been created, the 403 would be theatre.
        _factory.Email.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task AskingForASignInCodeFromAnotherSiteIsRefused()
    {
        // Worth its own case rather than being left to the group filter's
        // reputation. This is the only anonymous endpoint on the platform that
        // causes a message to be delivered to an address the caller chose, so a
        // cross-site page that could reach it would be a mail cannon anybody
        // could host.
        var client = _factory.CreateOriginlessClient();
        client.DefaultRequestHeaders.Add("Origin", "https://sw5e-phishing.example");

        var response = await client.PostAsJsonAsync(
            "/api/auth/email/code",
            new { email = AccountFlow.NewAddress("evil-origin-code") });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Refused before the handler ran, not after. A 403 issued once the
        // message was already on its way would be theatre.
        _factory.Email.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task SigningOutFromAnotherSiteIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "csrf-logout");

        await account.EstablishAsync(_factory.Email);

        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        forged.Headers.Add("Origin", "https://sw5e-phishing.example");

        var response = await client.SendAsync(forged);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Forced sign-out is a minor nuisance rather than a breach, but it is
        // the easiest possible check that the filter is applied to the whole
        // group rather than to the endpoints somebody remembered.
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AFormEncodedBodyIsRefused()
    {
        var client = _factory.CreateBrowserClient();

        // The only content types an HTML form can send cross-origin without CORS
        // approval are urlencoded, multipart and text/plain. Refusing all three
        // is what closes the last route a forged form could take, and it is the
        // framework's own binder that does it — the handler is never reached.
        var response = await client.PostAsync(
            "/api/auth/register",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = AccountFlow.NewAddress("form"),
                ["displayName"] = "Nobody",
            }));

        response.IsSuccessStatusCode.ShouldBeFalse();

        // The exact refusal depends on where in the pipeline the request dies —
        // the binder answers 415, and the deny-by-default authorization policy
        // can get there first — so the status is asserted as "refused" rather
        // than pinned to one code that a framework ordering change could move.
        response.StatusCode.ShouldBeOneOf(
            HttpStatusCode.UnsupportedMediaType,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden);

        // This is the assertion with teeth. If form binding ever started
        // working, the handler would run, an account would be created and a
        // verification link would go out — and no status-code check would
        // notice, because the endpoint answers 202 for everything.
        _factory.Email.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheContentApiIsStillAnonymous()
    {
        // The account work installs a fallback authorization policy that denies
        // anything which has not explicitly opted out. This is the check that
        // the public catalogue is one of the things that opted out — a
        // regression here would take the whole site offline for visitors.
        var client = _factory.CreateBrowserClient();

        (await client.GetAsync("/api/content-types")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RepeatedSignInAttemptsAreRefusedOnceTheBudgetIsSpent()
    {
        await using var throttled = new ThrottledAccountApiFactory(postgres);
        var client = throttled.CreateBrowserClient();

        var seen = new List<HttpStatusCode>();

        // The budget is three. The tenth attempt cannot be inside it however
        // the window happens to fall.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/passkey/login/complete",
                new { credential = new { id = "nope", type = "public-key" } });

            seen.Add(response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // A refusal has to tell a well-behaved client when to come
                // back, or it just converts a flood into a tighter loop.
                response.Headers.RetryAfter.ShouldNotBeNull();
                break;
            }
        }

        seen.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// The same host with the sensitive attempt budget turned down far enough
    /// that a test can spend it.
    /// </summary>
    private sealed class ThrottledAccountApiFactory(PostgresFixture postgres)
        : AccountApiFactory(postgres)
    {
        protected override int SensitiveAttempts => 3;
    }
}
