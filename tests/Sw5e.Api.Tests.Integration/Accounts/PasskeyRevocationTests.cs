using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Removing a credential from an account.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to enrolment, and the half that was missing. An account area
/// that can only ever add passkeys is not a smaller feature than one that can
/// also remove them; it is a security problem, because somebody whose device
/// was stolen has no way to stop that device signing in and no way to ask
/// anybody to do it for them.
/// </para>
/// <para>
/// The interesting boundary is the last credential. Passkeys are the only
/// credential this platform issues, so removing the final one does not lock an
/// account down, it strands it — the owner is locked out along with everybody
/// else, and the only route back is a recovery email that re-credentials the
/// account from scratch. So the endpoint refuses, and says why in a form the
/// front end can act on.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class PasskeyRevocationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ASecondCredentialCanBeRemovedAndStopsWorking()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "revoke");

        await account.EstablishAsync(_factory.Email);
        await account.EnrollPasskeyAsync();

        var before = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        before.GetProperty("passkeys").GetArrayLength().ShouldBe(2);

        var doomed = before.GetProperty("passkeys")[1].GetProperty("id").GetString()!;

        var removed = await client.DeleteAsync(
            $"/api/auth/passkey/{Uri.EscapeDataString(doomed)}");

        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await removed.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("removed");

        // Gone from the account's own view of itself...
        var after = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        after.GetProperty("passkeys").GetArrayLength().ShouldBe(1);
        after.GetProperty("passkeys").EnumerateArray()
            .Select(passkey => passkey.GetProperty("id").GetString())
            .ShouldNotContain(doomed);

        // ...and, which is the part that actually matters, no longer able to
        // sign in. Asserting only on the list would pass against an endpoint
        // that hid the credential without revoking it.
        await client.PostAsync("/api/auth/logout", content: null);

        var begin = await client.PostAsync("/api/auth/passkey/login/begin", content: null);
        var assertion = account.Authenticator.Get(
            await begin.Content.ReadAsStringAsync(),
            credentialId: doomed);

        var signIn = await client.PostAsJsonAsync(
            "/api/auth/passkey/login/complete",
            new { credential = assertion });

        signIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheLastCredentialIsRefusedRatherThanRemoved()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "revoke-last");

        await account.EstablishAsync(_factory.Email);

        var me = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        me.GetProperty("passkeys").GetArrayLength().ShouldBe(1);

        var only = me.GetProperty("passkeys")[0].GetProperty("id").GetString()!;

        var refused = await client.DeleteAsync(
            $"/api/auth/passkey/{Uri.EscapeDataString(only)}");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // A machine-readable reason, so the front end can say "add another one
        // first" rather than repeating a sentence it would have to keep in step
        // with the server's wording.
        var problem = await refused.ReadJsonAsync();
        problem.GetProperty("code").GetString().ShouldBe("last-credential");

        // And the credential really is still there and still works.
        var after = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        after.GetProperty("passkeys").GetArrayLength().ShouldBe(1);

        await client.PostAsync("/api/auth/logout", content: null);
        (await account.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnAnonymousCallerCannotRemoveAnything()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "revoke-anon");

        await account.EstablishAsync(_factory.Email);
        await account.EnrollPasskeyAsync();

        var me = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        var target = me.GetProperty("passkeys")[0].GetProperty("id").GetString()!;

        await client.PostAsync("/api/auth/logout", content: null);

        var response = await client.DeleteAsync(
            $"/api/auth/passkey/{Uri.EscapeDataString(target)}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// An enrolment ticket is permission to add a credential, never to remove
    /// one.
    /// </summary>
    /// <remarks>
    /// This is the property that keeps the recovery flow from being a way to
    /// strip an account. Somebody who intercepts a verification link can attach
    /// a credential of their own — that is the flow working as designed, and the
    /// owner is emailed about it — but they must not be able to remove the
    /// owner's existing credentials and take the account away entirely.
    /// </remarks>
    [Fact]
    public async Task AnEnrolmentTicketCannotRemoveACredential()
    {
        var established = _factory.CreateBrowserClient();
        var account = AccountFlow.For(established, "revoke-ticket");

        await account.EstablishAsync(_factory.Email);

        var me = await (await established.GetAsync("/api/auth/me")).ReadJsonAsync();
        var target = me.GetProperty("passkeys")[0].GetProperty("id").GetString()!;

        // A fresh browser holding nothing but a redeemed verification link.
        var ticketed = _factory.CreateBrowserClient();

        await ticketed.PostAsJsonAsync(
            "/api/auth/register",
            new { email = account.EmailAddress, displayName = "Ignored" });

        var verify = await ticketed.PostAsJsonAsync(
            "/api/auth/email/verify",
            new { email = account.EmailAddress, token = _factory.Email.LatestToken(account.EmailAddress) });

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The ticket does open the enrolment door...
        (await ticketed.PostAsync("/api/auth/passkey/register/begin", content: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...and does not open this one.
        var response = await ticketed.DeleteAsync(
            $"/api/auth/passkey/{Uri.EscapeDataString(target)}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACredentialBelongingToAnotherAccountIsNotFound()
    {
        var mine = _factory.CreateBrowserClient();
        var theirs = _factory.CreateBrowserClient();

        var owner = AccountFlow.For(theirs, "revoke-owner");
        await owner.EstablishAsync(_factory.Email);

        var ownerMe = await (await theirs.GetAsync("/api/auth/me")).ReadJsonAsync();
        var somebodyElses = ownerMe.GetProperty("passkeys")[0].GetProperty("id").GetString()!;

        var attacker = AccountFlow.For(mine, "revoke-attacker");
        await attacker.EstablishAsync(_factory.Email);
        await attacker.EnrollPasskeyAsync();

        var response = await mine.DeleteAsync(
            $"/api/auth/passkey/{Uri.EscapeDataString(somebodyElses)}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The other account is untouched.
        var still = await (await theirs.GetAsync("/api/auth/me")).ReadJsonAsync();
        still.GetProperty("passkeys").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task ACrossSiteRemovalIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "revoke-crosssite");

        await account.EstablishAsync(_factory.Email);
        await account.EnrollPasskeyAsync();

        var me = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        var target = me.GetProperty("passkeys")[0].GetProperty("id").GetString()!;

        using var forged = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/auth/passkey/{Uri.EscapeDataString(target)}");

        forged.Headers.Remove("Origin");
        forged.Headers.Add("Origin", "https://elsewhere.example");

        var response = await client.SendAsync(forged);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Nothing was removed.
        var after = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        after.GetProperty("passkeys").GetArrayLength().ShouldBe(2);
    }
}
