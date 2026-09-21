using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// The rule that the actions changing what other accounts may do want a factor
/// proved minutes ago, not merely at some point during the session.
/// </summary>
/// <remarks>
/// <para>
/// A session lasts a working day, which is right for reading and writing rules
/// pages and wrong for handing somebody the Administrator role. Between nine in
/// the morning and five in the afternoon sits the unattended laptop, and the
/// whole value of a passkey is that the person holding it was present. So three
/// routes ask again: the role grant, the suspension switch and the deletion.
/// </para>
/// <para>
/// Two properties are being defended here and they pull in opposite directions,
/// which is why both are tested. A stale session must be refused, or the rule
/// is a sentence in a document. And a session that has just proved a factor
/// must work, including across the revalidation the cookie quietly performs
/// every few minutes, or the rule is an administrator who cannot administer.
/// See <see cref="AProvedFactorSurvivesTheSessionBeingRevalidated"/>, which is
/// a regression test for exactly that: the framework rebuilds the principal
/// from the account and drops every claim describing the sign-in, so before the
/// claims were carried across, a passkey stopped counting five minutes after it
/// was used.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class RecentAuthenticationTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>
    /// Comfortably past the ten minute window, and past the five minute
    /// revalidation interval on the way.
    /// </summary>
    private static readonly TimeSpan PastTheWindow = TimeSpan.FromMinutes(11);

    /// <summary>
    /// Past the revalidation interval and inside the window. The gap where a
    /// dropped claim and an expired window look identical from the outside, and
    /// where they have to be told apart.
    /// </summary>
    private static readonly TimeSpan PastRevalidation = TimeSpan.FromMinutes(6);

    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AFreshlyProvedFactorMayGrantARole()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "recent-fresh-admin");
        var target = await TargetAsync("recent-fresh-target");

        var granted = await GrantContributorAsync(client, target);

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOfAsync(target)).ShouldContain(Sw5eRoles.Contributor);
    }

    /// <summary>
    /// The regression test. A passkey proved six minutes ago is still a passkey
    /// proved six minutes ago, whatever the cookie did in between.
    /// </summary>
    /// <remarks>
    /// The session is revalidated against the account's security stamp every
    /// five minutes, and that revalidation rebuilds the principal from the
    /// store, which knows about the account and nothing about this sign-in.
    /// Every claim describing the sign-in is therefore dropped unless it is
    /// deliberately carried across. The symptom was an administrator being told
    /// to sign in with a passkey by a page listing the passkey they had signed
    /// in with, five minutes earlier.
    /// </remarks>
    [Fact]
    public async Task AProvedFactorSurvivesTheSessionBeingRevalidated()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "recent-revalidated-admin");
        var target = await TargetAsync("recent-revalidated-target");

        _factory.Clock.Advance(PastRevalidation);

        var granted = await GrantContributorAsync(client, target);

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOfAsync(target)).ShouldContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task AFactorProvedThisMorningMayNotGrantARole()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "recent-stale-admin");
        var target = await TargetAsync("recent-stale-target");

        _factory.Clock.Advance(PastTheWindow);

        var refused = await GrantContributorAsync(client, target);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Machine-readable, and distinct from the refusal that means "you have
        // no second factor at all". The remedies are different and only one of
        // them is available to this account, which already holds a passkey and
        // has already used it.
        (await refused.ReadJsonAsync())
            .GetProperty("code").GetString()
            .ShouldBe(Sw5eIdentityServiceCollectionExtensions.RecentAuthenticationRequired);

        // The refusal was real rather than cosmetic.
        (await RolesOfAsync(target)).ShouldNotContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task ConfirmingIdentityClearsTheRefusal()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = await AdministrationFlow.AdministratorAsync(
            _factory, client, "recent-confirm-admin");

        var target = await TargetAsync("recent-confirm-target");

        _factory.Clock.Advance(PastTheWindow);
        (await GrantContributorAsync(client, target)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        // The same session, the same passkey, proved again. Nothing is signed
        // out and nothing is re-enrolled.
        var confirmed = await administrator.ReauthenticateAsync(client);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var granted = await GrantContributorAsync(client, target);

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOfAsync(target)).ShouldContain(Sw5eRoles.Contributor);
    }

    /// <summary>
    /// Confirmation restarts the window rather than extending the session's
    /// original claim.
    /// </summary>
    [Fact]
    public async Task ConfirmingIdentityStartsTheWindowAgain()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = await AdministrationFlow.AdministratorAsync(
            _factory, client, "recent-restart-admin");

        var target = await TargetAsync("recent-restart-target");

        _factory.Clock.Advance(PastTheWindow);
        (await administrator.ReauthenticateAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Another stretch that would have been fatal measured from the original
        // sign-in, and is not measured from there any more.
        _factory.Clock.Advance(PastRevalidation);

        (await GrantContributorAsync(client, target)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Reading is deliberately not gated, and this is the test that would fail
    /// if somebody later decided to gate it.
    /// </summary>
    /// <remarks>
    /// Working out whether an account should be suspended begins by reading
    /// about it. A site that asked for a fingerprint before it would show a
    /// list would teach its administrators to confirm without reading, which is
    /// precisely the reflex the prompt relies on not existing.
    /// </remarks>
    [Fact]
    public async Task ReadingTheDirectoryDoesNotAskForConfirmation()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "recent-reading-admin");

        _factory.Clock.Advance(PastTheWindow);

        (await client.GetAsync("/api/auth/admin/users")).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/api/auth/admin/audit")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SuspendingAsksForConfirmation()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "recent-suspend-admin");
        var target = await TargetAsync("recent-suspend-target");

        _factory.Clock.Advance(PastTheWindow);

        var refused = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/suspension",
            new { suspended = true, reason = "Testing the confirmation gate." });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ReadJsonAsync())
            .GetProperty("code").GetString()
            .ShouldBe(Sw5eIdentityServiceCollectionExtensions.RecentAuthenticationRequired);

        (await AdministrationFlow.IsSuspendedAsync(_factory, target)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeletingAsksForConfirmation()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "recent-delete-admin");
        var target = await TargetAsync("recent-delete-target");

        _factory.Clock.Advance(PastTheWindow);

        var refused = await client.DeleteAsync($"/api/auth/admin/users/{target}");

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ReadJsonAsync())
            .GetProperty("code").GetString()
            .ShouldBe(Sw5eIdentityServiceCollectionExtensions.RecentAuthenticationRequired);

        (await AdministrationFlow.ExistsAsync(_factory, target)).ShouldBeTrue();
    }

    /// <summary>
    /// An account with no second factor on its session is told to get one,
    /// not to confirm one it has not got.
    /// </summary>
    /// <remarks>
    /// The two refusals wear the same status code and want different answers
    /// from the reader. Sending somebody who signed in through a mailbox off to
    /// press a button labelled "confirm with your passkey" would be a dead end
    /// with a friendly caption, which is the failure the earlier version of this
    /// area already made once.
    /// </remarks>
    [Fact]
    public async Task AMailboxSessionIsToldToSignInStronglyRatherThanToConfirm()
    {
        var administrator = await AdministrationFlow.AdministratorAsync(
            _factory, _factory.CreateBrowserClient(), "recent-mailbox-admin");

        var target = await TargetAsync("recent-mailbox-target");

        var weak = _factory.CreateBrowserClient();
        await AdministrationFlow.SignInWithEmailedCodeAsync(
            _factory, weak, administrator.EmailAddress);

        _factory.Clock.Advance(PastRevalidation);

        var refused = await GrantContributorAsync(weak, target);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ReadJsonAsync())
            .GetProperty("code").GetString()
            .ShouldBe(Sw5eIdentityServiceCollectionExtensions.StrongAuthenticationRequired);
    }

    private async Task<Guid> TargetAsync(string label)
    {
        var account = await AdministrationFlow.MemberAsync(
            _factory, _factory.CreateBrowserClient(), label);

        return await AdministrationFlow.IdOfAsync(_factory, account.EmailAddress);
    }

    private static Task<HttpResponseMessage> GrantContributorAsync(HttpClient client, Guid target) =>
        client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

    private async Task<IList<string>> RolesOfAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        var users = scope.ServiceProvider
            .GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException($"No account exists for {userId}.");

        return await users.GetRolesAsync(user);
    }
}
