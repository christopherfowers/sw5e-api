using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// That a suspension is a fact about an account rather than a flag on a screen.
/// </summary>
/// <remarks>
/// <para>
/// The tests that matter here are the two that make the difference between
/// suspension and theatre. Refusing a fresh sign-in is the easy half and the
/// half everybody implements;
/// <see cref="ASuspendedAccountsOpenSessionStopsWorkingImmediately"/> is the
/// other one — the person a suspension is aimed at is by definition somebody
/// who is doing something right now, which means they already have a session
/// cookie in a tab, and a suspension that waited for it to expire would leave
/// them eight hours to keep doing it.
/// </para>
/// <para>
/// <see cref="ASuspendedAccountCannotSignInWithItsPasskey"/> is the second: the
/// credential is left on the account deliberately, so that reinstating somebody
/// does not require re-credentialling them, and a design that leaves the
/// credential in place has to prove that the credential does not work.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AccountSuspensionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ASuspendedAccountCannotSignInWithItsPasskey()
    {
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "suspend-admin");

        var victimClient = _factory.CreateBrowserClient();
        var victim = await AdministrationFlow.MemberAsync(_factory, victimClient, "suspend-victim");
        var victimId = await AdministrationFlow.IdOfAsync(_factory, victim.EmailAddress);

        // The control, first. This account can sign in, so what changes below
        // is the suspension and not the fixture.
        await victimClient.PostAsync("/api/auth/logout", content: null);
        (await victim.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        await victimClient.PostAsync("/api/auth/logout", content: null);

        var suspended = await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = "Posting other people's addresses in flag details." });

        suspended.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdministrationFlow.IsSuspendedAsync(_factory, victimId)).ShouldBeTrue();

        // The passkey is still on the account, and the assertion it produces is
        // still cryptographically valid. It gets the same unhelpful 401 as
        // every other failed sign-in, because saying "suspended" would confirm
        // to a stranger that the account exists.
        var refused = await victim.SignInAsync();

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And no session came out of it.
        (await victimClient.GetAsync("/api/auth/me"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ASuspendedAccountCannotSignInWithAnEmailedCodeEither()
    {
        // The other door in. A suspension that only closed the passkey route
        // would be a suspension anybody could walk around by asking for a code.
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "suspend-code-admin");

        var victim = await AdministrationFlow.MemberAsync(
            _factory, _factory.CreateBrowserClient(), "suspend-code-victim");

        var victimId = await AdministrationFlow.IdOfAsync(_factory, victim.EmailAddress);

        await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = "Under investigation." });

        var client = _factory.CreateBrowserClient();

        // The request for a code still answers 202, because that endpoint
        // answers 202 for every address it can parse — including addresses with
        // no account at all. Making it refuse for a suspended account would
        // turn it into a way to test whether a given person is suspended.
        var requested = await client.PostAsJsonAsync(
            "/api/auth/email/code", new { email = victim.EmailAddress });

        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Suspending discards any code already outstanding, so there may be
        // none to read; the one that matters is that redeeming a freshly issued
        // one is refused.
        var code = _factory.Email.LatestSignInCode(victim.EmailAddress);

        var verified = await client.PostAsJsonAsync(
            "/api/auth/email/code/verify",
            new { email = victim.EmailAddress, code });

        verified.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ASuspendedAccountsOpenSessionStopsWorkingImmediately()
    {
        // The test this whole feature turns on. Suspending somebody who is
        // signed in has to end the session they are holding, not the next one
        // they try to open.
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "suspend-live-admin");

        var victimClient = _factory.CreateBrowserClient();
        var victim = await AdministrationFlow.MemberAsync(
            _factory, victimClient, "suspend-live-victim");

        var victimId = await AdministrationFlow.IdOfAsync(_factory, victim.EmailAddress);

        // The session is open and working, on a client that is going to keep
        // using the very same cookie.
        (await victimClient.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = "Deleting other people's drafts." }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // No sign-out, no waiting, no clock advanced. The very next request on
        // the same cookie is refused.
        //
        // The security stamp rotation that suspension also performs would get
        // here eventually — the stamp validator re-checks every five minutes —
        // and eventually is exactly what is not good enough. Nothing in this
        // test moves the clock, so a five-minute mechanism cannot be what makes
        // it pass.
        (await victimClient.GetAsync("/api/auth/me"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReinstatingAnAccountGivesBackTheSessionItCouldHaveHad()
    {
        // The control for all three refusals above. An implementation that
        // refused a suspended account by refusing everybody would pass every
        // one of them and fail this.
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "reinstate-admin");

        var victimClient = _factory.CreateBrowserClient();
        var victim = await AdministrationFlow.MemberAsync(
            _factory, victimClient, "reinstate-victim");

        var victimId = await AdministrationFlow.IdOfAsync(_factory, victim.EmailAddress);

        await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = "A misunderstanding, as it turns out." });

        (await victim.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var lifted = await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = false, reason = (string?)null });

        lifted.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await lifted.ReadJsonAsync())
            .GetProperty("suspension").ValueKind
            .ShouldBe(System.Text.Json.JsonValueKind.Null);

        (await AdministrationFlow.IsSuspendedAsync(_factory, victimId)).ShouldBeFalse();

        // With the passkey it already had. Suspension leaves credentials alone
        // precisely so that reinstating somebody is not a re-credentialling
        // exercise, and this is what makes that claim true rather than stated.
        var restored = await victim.SignInAsync();

        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await victimClient.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SuspendingWithoutAReasonIsRefused()
    {
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "suspend-reasonless-admin");

        var victimId = await AdministrationFlow.IdOfAsync(
            _factory,
            (await AdministrationFlow.MemberAsync(
                _factory, _factory.CreateBrowserClient(), "suspend-reasonless")).EmailAddress);

        var response = await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = (string?)null });

        // A suspension nobody wrote a reason for is one the next administrator
        // cannot review and nobody can defend.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AdministrationFlow.IsSuspendedAsync(_factory, victimId)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnAdministratorCannotSuspendOrDeleteTheirOwnAccount()
    {
        // The companion to the rule that already refuses self-demotion. With
        // all three closed, the number of administrators cannot reach zero
        // through this API — which also removes the most attractive move
        // available to somebody who has just stolen an administrator's session.
        var client = _factory.CreateBrowserClient();
        var administrator = await AdministrationFlow.AdministratorAsync(
            _factory, client, "suspend-self");

        var id = await AdministrationFlow.IdOfAsync(_factory, administrator.EmailAddress);

        var suspension = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{id}/suspension",
            new { suspended = true, reason = "Locking myself out for the evening." });

        suspension.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AdministrationFlow.IsSuspendedAsync(_factory, id)).ShouldBeFalse();

        var deletion = await client.DeleteAsync($"/api/auth/admin/users/{id}");

        deletion.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AdministrationFlow.ExistsAsync(_factory, id)).ShouldBeTrue();

        // The session is intact throughout: neither refusal was a refusal to
        // authorise the caller.
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SuspendingAnAlreadySuspendedAccountIsRefused()
    {
        // Restating the current state is almost always two administrators
        // acting on the same account, and answering 200 to the second would
        // tell them they did something they did not do — and would move the
        // recorded suspension onto their name.
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "suspend-twice-admin");

        var victimId = await AdministrationFlow.IdOfAsync(
            _factory,
            (await AdministrationFlow.MemberAsync(
                _factory, _factory.CreateBrowserClient(), "suspend-twice")).EmailAddress);

        (await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = "First." }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = "Second." }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheAccountIsToldItWasSuspendedAndNotToldWhy()
    {
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "suspend-notice-admin");

        var victim = await AdministrationFlow.MemberAsync(
            _factory, _factory.CreateBrowserClient(), "suspend-notice");

        var victimId = await AdministrationFlow.IdOfAsync(_factory, victim.EmailAddress);

        const string Reason = "Suspected of running the scraper hitting the powers index.";

        await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{victimId}/suspension",
            new { suspended = true, reason = Reason });

        var notice = _factory.Email
            .For(victim.EmailAddress)
            .Last(message => message.Kind == AccountMessageKind.SecurityNotice);

        notice.Body.ShouldContain("suspended");

        // Never the reason. It is written for the other administrators, and
        // where the reason is an investigation, quoting it back would tell the
        // subject what is being investigated.
        notice.Body.ShouldNotContain("scraper");

        // The reason is disclosed on the administrative surface, which is what
        // it was written for.
        var detail = await admin.GetAsync($"/api/auth/admin/users/{victimId}");

        detail.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await detail.ReadJsonAsync())
            .GetProperty("user").GetProperty("suspension").GetProperty("reason").GetString()
            .ShouldBe(Reason);
    }
}
