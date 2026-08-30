using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// The sign-in path: that it works, and that each of the things it is supposed
/// to refuse is actually refused.
/// </summary>
/// <remarks>
/// Every negative test here checks two things — the status code, and that no
/// session was issued. The second is the one that matters. An endpoint can
/// return 401 and still have called SignInAsync, and a test that only read the
/// status code would pass against exactly that bug.
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class PasskeyAuthenticationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task RegisteringVerifyingAndEnrollingAPasskeyProducesAWorkingSession()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "happy");

        await account.EstablishAsync(_factory.Email);

        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await me.ReadJsonAsync();
        body.GetProperty("email").GetString().ShouldBe(account.EmailAddress);
        body.GetProperty("passkeyCount").GetInt32().ShouldBe(1);
        body.GetProperty("twoFactorEnabled").GetBoolean().ShouldBeFalse();

        // The default role, granted at registration. Without it, authorization
        // has no positive grant to reason about.
        body.GetProperty("roles").EnumerateArray()
            .Select(role => role.GetString())
            .ShouldContain("Community");
    }

    [Fact]
    public async Task CurrentUserWithoutASessionIsRefused()
    {
        var response = await _factory.CreateBrowserClient().GetAsync("/api/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheSessionCookieIsHttpOnlySecureAndStrictlySameSite()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "cookie");

        await account.RegisterAsync();
        await account.VerifyEmailAsync(_factory.Email);
        await account.EnrollPasskeyAsync();

        var signIn = await account.SignInAsync();
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cookie = signIn.Headers.GetValues("Set-Cookie")
            .SingleOrDefault(value => value.StartsWith("__Host-sw5e.session=", StringComparison.Ordinal));

        // The name itself is an assertion: a browser refuses to store a
        // __Host- cookie that is not Secure, has no Path=/ or carries a Domain,
        // so the prefix enforces the attributes independently of this server.
        cookie.ShouldNotBeNull();
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
        cookie.ShouldNotContain("domain=", Case.Insensitive);
    }

    [Fact]
    public async Task ACredentialSignedForAnotherOriginIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "origin");

        await account.EstablishAsync(_factory.Email);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The signature is valid, the credential is registered, and the
        // challenge is the one this server just issued. The only thing wrong is
        // that the authenticator was told it was signing for somebody else's
        // site — which is precisely the phishing case WebAuthn exists to stop.
        var response = await account.SignInAsync(originOverride: "https://sw5e-phishing.example");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldHaveNoSessionAsync(client);
    }

    [Fact]
    public async Task ReplayingASpentAssertionIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "replay");

        await account.EstablishAsync(_factory.Email);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Capture one complete, genuine assertion.
        var begin = await client.PostAsync("/api/auth/passkey/login/begin", content: null);
        var credential = account.Authenticator.Get(await begin.Content.ReadAsStringAsync());

        var first = await client.PostAsJsonAsync("/api/auth/passkey/login/complete", new { credential });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        await client.PostAsync("/api/auth/logout", content: null);

        // Present the very same bytes again. The challenge cookie was cleared
        // when it was answered, so there is nothing to verify against; even had
        // it survived, the signature counter has moved past this assertion.
        var replayed = await client.PostAsJsonAsync("/api/auth/passkey/login/complete", new { credential });

        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldHaveNoSessionAsync(client);
    }

    [Fact]
    public async Task AnAssertionWithNoChallengeInFlightIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "nochallenge");

        await account.EstablishAsync(_factory.Email);
        await client.PostAsync("/api/auth/logout", content: null);

        // Take a challenge on one connection and answer it without ever having
        // asked for one on this connection: a fresh client holds no challenge
        // cookie, so the assertion has nothing to be checked against.
        var begin = await client.PostAsync("/api/auth/passkey/login/begin", content: null);
        var credential = account.Authenticator.Get(await begin.Content.ReadAsStringAsync());

        var stranger = _factory.CreateBrowserClient();
        var response = await stranger.PostAsJsonAsync("/api/auth/passkey/login/complete", new { credential });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldHaveNoSessionAsync(stranger);
    }

    [Fact]
    public async Task SigningOutEndsTheSession()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "logout");

        await account.EstablishAsync(_factory.Email);

        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The check that matters: the cookie is genuinely gone, rather than the
        // endpoint merely having answered politely.
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheSignInChallengeNamesNoCredentials()
    {
        var client = _factory.CreateBrowserClient();
        await AccountFlow.For(client, "enum-challenge").EstablishAsync(_factory.Email);

        var begin = await client.PostAsync("/api/auth/passkey/login/begin", content: null);
        var options = await begin.ReadJsonAsync();

        // An empty allowCredentials list is what makes sign-in enumeration-proof:
        // the server is never told whose account is being signed into, so there
        // is no input to vary and no response to compare. A populated list would
        // mean the endpoint had taken an identifier and answered differently
        // depending on whether it existed.
        options.GetProperty("allowCredentials").GetArrayLength().ShouldBe(0);
        options.GetProperty("userVerification").GetString().ShouldBe("required");
    }

    private static async Task ShouldHaveNoSessionAsync(HttpClient client) =>
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
}
