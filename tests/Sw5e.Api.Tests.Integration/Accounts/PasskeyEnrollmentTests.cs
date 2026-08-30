using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Who is allowed to attach a credential to an account, and for how long.
/// </summary>
/// <remarks>
/// This is the flow with the widest blast radius on the platform: anything that
/// can enrol a passkey on somebody else's account owns that account outright.
/// The tests here are all about the boundary of the enrolment window rather
/// than about the ceremony, which is covered elsewhere.
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class PasskeyEnrollmentTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task EnrolmentIsRefusedWithoutASessionOrAVerifiedAddress()
    {
        var response = await _factory.CreateBrowserClient()
            .PostAsync("/api/auth/passkey/register/begin", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegisteringWithoutVerifyingDoesNotOpenTheEnrolmentWindow()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "unverified");

        await account.RegisterAsync();

        // The account exists and its address has been mailed a link. Until that
        // link is redeemed, nothing about this browser is entitled to attach a
        // credential to it.
        var response = await client.PostAsync("/api/auth/passkey/register/begin", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATamperedVerificationTokenDoesNotOpenTheEnrolmentWindow()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "tampered");

        await account.RegisterAsync();

        var token = _factory.Email.LatestToken(account.EmailAddress);

        // One character different. The token is data-protected and carries the
        // account's security stamp, so any edit invalidates it.
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        var verify = await client.PostAsJsonAsync(
            "/api/auth/email/verify", new { email = account.EmailAddress, token = tampered });

        verify.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var begin = await client.PostAsync("/api/auth/passkey/register/begin", content: null);
        begin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AVerificationTokenCannotBeRedeemedTwice()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "single-use");

        await account.RegisterAsync();

        var token = _factory.Email.LatestToken(account.EmailAddress);

        var first = await client.PostAsJsonAsync(
            "/api/auth/email/verify", new { email = account.EmailAddress, token });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Redeeming rotates the account's security stamp, and the token is
        // derived from it. A link intercepted in transit is therefore worthless
        // the moment the real owner uses theirs.
        var second = await client.PostAsJsonAsync(
            "/api/auth/email/verify", new { email = account.EmailAddress, token });

        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheEnrolmentWindowClosesOnceItHasBeenUsed()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "window-closes");

        await account.RegisterAsync();
        await account.VerifyEmailAsync(_factory.Email);
        await account.EnrollPasskeyAsync();

        // The ticket is consumed by the enrolment it authorised. Without this,
        // one emailed link would be good for any number of credentials for the
        // next ten minutes.
        var again = await client.PostAsync("/api/auth/passkey/register/begin", content: null);

        again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IssuingAFreshRecoveryLinkInvalidatesTheOutstandingOne()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "stale-ticket");

        await account.RegisterAsync();
        var first = _factory.Email.LatestToken(account.EmailAddress);

        // A second request — from anybody, since the endpoint is anonymous —
        // rotates nothing yet, but issues a newer token.
        await account.RegisterAsync();
        var second = _factory.Email.LatestToken(account.EmailAddress);

        first.ShouldNotBe(second);

        // Redeeming the newer one rotates the security stamp.
        var redeemed = await client.PostAsJsonAsync(
            "/api/auth/email/verify", new { email = account.EmailAddress, token = second });
        redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Which leaves the older one dead, even though it was valid when issued
        // and has not expired.
        var stale = await client.PostAsJsonAsync(
            "/api/auth/email/verify", new { email = account.EmailAddress, token = first });

        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ASignedInAccountCanEnrolASecondPasskey()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "second-device");

        await account.EstablishAsync(_factory.Email);

        // A signed-in caller needs no ticket: the session is the authorisation.
        // This is how somebody adds their laptop after registering on a phone.
        await account.EnrollPasskeyAsync();

        var me = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        me.GetProperty("passkeys").GetArrayLength().ShouldBe(2);

        account.Authenticator.CredentialIds.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EnrollingAPasskeyEmailsTheAccountHolder()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "notice");

        await account.EstablishAsync(_factory.Email);

        // The message that lets somebody notice a takeover they did not
        // perform. It goes to the address on file rather than to whoever is
        // holding the browser.
        _factory.Email.For(account.EmailAddress)
            .ShouldContain(message =>
                message.Kind == AccountMessageKind.SecurityNotice &&
                message.Body.Contains("passkey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnrolmentDoesNotByItselfSignTheAccountIn()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "no-implicit-session");

        await account.RegisterAsync();
        await account.VerifyEmailAsync(_factory.Email);
        await account.EnrollPasskeyAsync();

        // Deliberate: a passkey assertion is the only thing on this platform
        // that issues a session. Signing somebody in here would create a second
        // route into an account, and that route would skip the second factor.
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var signIn = await account.SignInAsync();
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
