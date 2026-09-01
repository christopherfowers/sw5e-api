using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Raising a session that was opened with an emailed code, by proving a
/// credential the account already holds.
/// </summary>
/// <remarks>
/// <para>
/// The rule these tests sit next to is the one in <see cref="ElevatedRoleTests"/>:
/// an elevated role is unusable from a session that only proved mailbox
/// control. That rule was correct and, on its own, produced a genuine dead end
/// — an administrator with a passkey on the desk in front of them, signed in by
/// code, was told to go and add a passkey. These tests are about the way out of
/// it, and about the way out not being a way around it.
/// </para>
/// <para>
/// <see cref="APasskeyBelongingToAnotherAccountDoesNotRaiseTheSession"/> is the
/// one that matters. Everything else here would pass against an implementation
/// that raised any session presented with any valid signature.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class ReauthenticationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AnAdministratorWhoSignedInWithACodeCanAdministerAfterProvingTheirPasskey()
    {
        var administrator = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-admin");
        await PromoteAsync(administrator.EmailAddress, Sw5eRoles.Administrator);

        var target = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-target");
        var targetId = await IdOf(target.EmailAddress);

        // In through the weak door, on a client that has never held anything
        // stronger.
        var session = _factory.CreateBrowserClient();

        (await administrator.SignInWithEmailedCodeAsync(session, _factory.Email))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var beforeRaising = await session.PutAsJsonAsync(
            $"/api/auth/admin/users/{targetId}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

        // The state the reported bug leaves an administrator in.
        beforeRaising.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var raised = await administrator.ReauthenticateAsync(session);
        raised.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The response says what the session is now, so the browser does not
        // have to re-fetch the profile to find out whether it worked.
        (await raised.ReadJsonAsync())
            .GetProperty("authenticationMethod").GetString().ShouldBe("passkey");

        var afterRaising = await session.PutAsJsonAsync(
            $"/api/auth/admin/users/{targetId}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

        afterRaising.StatusCode.ShouldBe(HttpStatusCode.OK);

        // And it took effect, rather than merely being answered with a 200.
        (await RolesOf(target.EmailAddress)).ShouldContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task TheProfileReportsTheRaisedSession()
    {
        var account = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-profile");

        var session = _factory.CreateBrowserClient();
        await account.SignInWithEmailedCodeAsync(session, _factory.Email);

        var weak = await (await session.GetAsync("/api/auth/me")).ReadJsonAsync();
        weak.GetProperty("strongAuthentication").GetBoolean().ShouldBeFalse();
        weak.GetProperty("authenticationMethod").GetString().ShouldBe("email");

        (await account.ReauthenticateAsync(session)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var strong = await (await session.GetAsync("/api/auth/me")).ReadJsonAsync();
        strong.GetProperty("strongAuthentication").GetBoolean().ShouldBeTrue();
        strong.GetProperty("authenticationMethod").GetString().ShouldBe("passkey");

        // Same account, not a different one. Re-issuing the cookie is a
        // rewrite of the whole ticket, so this is worth pinning: a mistake in
        // that rewrite could hand the caller somebody else's identity.
        strong.GetProperty("email").GetString().ShouldBe(account.EmailAddress);
    }

    [Fact]
    public async Task APasskeyBelongingToAnotherAccountDoesNotRaiseTheSession()
    {
        // The attack this endpoint would otherwise open. Somebody who has taken
        // over a mailbox holds a session for the account they are attacking and
        // a passkey for an account of their own. If a valid signature were
        // enough, their own credential would raise the stolen session.
        var victim = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-victim");
        await PromoteAsync(victim.EmailAddress, Sw5eRoles.Administrator);

        var attacker = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-attacker");

        var stolen = _factory.CreateBrowserClient();
        await victim.SignInWithEmailedCodeAsync(stolen, _factory.Email);

        var begin = await stolen.PostAsync("/api/auth/reauthenticate/passkey/begin", content: null);
        begin.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The victim's challenge, answered with the attacker's authenticator.
        // The signature is genuine; it is simply not this account's.
        var credential = attacker.Authenticator.Get(await begin.Content.ReadAsStringAsync());

        var refused = await stolen.PostAsJsonAsync(
            "/api/auth/reauthenticate/passkey/complete",
            new { credential });

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Still weak, and still refused by the thing the raise was wanted for.
        var profile = await (await stolen.GetAsync("/api/auth/me")).ReadJsonAsync();
        profile.GetProperty("strongAuthentication").GetBoolean().ShouldBeFalse();
        profile.GetProperty("email").GetString().ShouldBe(victim.EmailAddress);

        (await stolen.GetAsync("/api/auth/admin/users"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ASpentChallengeCannotBeAnsweredTwice()
    {
        var account = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-replay");

        var session = _factory.CreateBrowserClient();
        await account.SignInWithEmailedCodeAsync(session, _factory.Email);

        var begin = await session.PostAsync("/api/auth/reauthenticate/passkey/begin", content: null);
        var credential = account.Authenticator.Get(await begin.Content.ReadAsStringAsync());

        (await session.PostAsJsonAsync(
            "/api/auth/reauthenticate/passkey/complete", new { credential }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The same assertion, replayed. The challenge cookie was spent by the
        // first attempt, so there is nothing left for this one to match.
        (await session.PostAsJsonAsync(
            "/api/auth/reauthenticate/passkey/complete", new { credential }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoneOfItIsAWayIntoAnAccount()
    {
        // Every route here requires a session. If any of them did not, the
        // endpoint would be a sign-in that skipped the sign-in.
        var anonymous = _factory.CreateBrowserClient();

        (await anonymous.PostAsync("/api/auth/reauthenticate/passkey/begin", content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await anonymous.PostAsJsonAsync(
            "/api/auth/reauthenticate/passkey/complete", new { credential = (object?)null }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await anonymous.PostAsJsonAsync(
            "/api/auth/reauthenticate/totp", new { code = "123456" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnAuthenticatorCodeIsRefusedWhenThereIsNoAuthenticator()
    {
        // Otherwise the endpoint would quietly do nothing on the accounts most
        // likely to reach for it, and read as a broken code entry rather than
        // as "you have not set one up".
        var account = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-no-totp");

        var session = _factory.CreateBrowserClient();
        await account.SignInWithEmailedCodeAsync(session, _factory.Email);

        var refused = await session.PostAsJsonAsync(
            "/api/auth/reauthenticate/totp", new { code = "123456" });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await refused.ReadJsonAsync())
            .GetProperty("detail").GetString().ShouldContain("authenticator app");
    }

    [Fact]
    public async Task AWrongAuthenticatorCodeCountsAgainstTheLockout()
    {
        // The endpoint takes a six-digit guess from anybody holding a session.
        // Uncounted, that is an unmetered oracle, and a stolen mailbox would
        // eventually be enough to raise the session after all.
        var account = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-lockout");

        var enrolled = _factory.CreateBrowserClient();
        await account.SignInWithEmailedCodeAsync(enrolled, _factory.Email);
        await EnrollAuthenticatorAsync(enrolled, account.EmailAddress);

        var before = await AccessFailedCountOf(account.EmailAddress);

        (await enrolled.PostAsJsonAsync("/api/auth/reauthenticate/totp", new { code = "000000" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await AccessFailedCountOf(account.EmailAddress)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task AnAuthenticatorCodeRaisesTheSession()
    {
        // The control for the two refusals above, and the route for somebody
        // whose device cannot do WebAuthn at all.
        var account = await EstablishAsync(_factory.CreateBrowserClient(), "reauth-totp");
        await PromoteAsync(account.EmailAddress, Sw5eRoles.Administrator);

        var session = _factory.CreateBrowserClient();
        await account.SignInWithEmailedCodeAsync(session, _factory.Email);

        var secret = await EnrollAuthenticatorAsync(session, account.EmailAddress);

        // Enrolment leaves the session as it found it: switching two-factor
        // authentication on is not the same as having demonstrated it during
        // this sign-in, and treating it as such would be the bug this whole
        // file exists to avoid.
        (await (await session.GetAsync("/api/auth/me")).ReadJsonAsync())
            .GetProperty("strongAuthentication").GetBoolean().ShouldBeFalse();

        var raised = await session.PostAsJsonAsync(
            "/api/auth/reauthenticate/totp",
            new { code = TimeBasedOneTimePassword.Generate(secret) });

        raised.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await raised.ReadJsonAsync())
            .GetProperty("authenticationMethod").GetString().ShouldBe("totp");

        (await session.GetAsync("/api/auth/admin/users"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Runs a full authenticator enrolment and returns the shared secret.
    /// </summary>
    private async Task<string> EnrollAuthenticatorAsync(HttpClient session, string emailAddress)
    {
        var enroll = await session.PostAsync("/api/auth/mfa/totp/enroll", content: null);
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secret = (await enroll.ReadJsonAsync()).GetProperty("sharedKey").GetString();
        secret.ShouldNotBeNull();

        var verified = await session.PostAsJsonAsync(
            "/api/auth/mfa/totp/verify",
            new { code = TimeBasedOneTimePassword.Generate(secret) });

        verified.StatusCode.ShouldBe(HttpStatusCode.OK);

        return secret;
    }

    private async Task<AccountFlow> EstablishAsync(HttpClient client, string label) =>
        await AccountFlow.For(client, label).EstablishAsync(_factory.Email);

    private async Task PromoteAsync(string emailAddress, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress);
        user.ShouldNotBeNull();

        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
    }

    private async Task<Guid> IdOf(string emailAddress)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress);
        user.ShouldNotBeNull();

        return user.Id;
    }

    private async Task<IList<string>> RolesOf(string emailAddress)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress);
        user.ShouldNotBeNull();

        return await users.GetRolesAsync(user);
    }

    private async Task<int> AccessFailedCountOf(string emailAddress)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress);
        user.ShouldNotBeNull();

        return await users.GetAccessFailedCountAsync(user);
    }
}
