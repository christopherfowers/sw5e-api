using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// The rule that an elevated role can only be exercised from a session
/// established with a passkey or an authenticator code.
/// </summary>
/// <remarks>
/// <para>
/// Adding a mailbox code as a way in created a problem the platform did not
/// have while passkeys were the only credential: mailbox control is the thing
/// every other account on the internet is recovered through, so an
/// administrator whose email is compromised would otherwise be an administrator
/// whose site is compromised — even if they had diligently enrolled a passkey,
/// because the weaker route would still be open.
/// </para>
/// <para>
/// The tests below are therefore about refusal. The one that matters most is
/// <see cref="AnAdministratorWhoSignedInWithACodeCannotAdminister"/>: it is the
/// difference between the rule being enforced and the rule being a sentence in
/// a document.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class ElevatedRoleTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AnAdministratorWhoSignedInWithACodeCannotAdminister()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = await EstablishAsync(client, "elevated-admin");
        await PromoteAsync(administrator.EmailAddress, Sw5eRoles.Administrator);

        var target = await EstablishAsync(_factory.CreateBrowserClient(), "elevated-target");

        await client.PostAsync("/api/auth/logout", content: null);

        // In through the weak door.
        var weak = _factory.CreateBrowserClient();
        await weak.PostAsJsonAsync("/api/auth/email/code", new { email = administrator.EmailAddress });

        var signIn = await weak.PostAsJsonAsync(
            "/api/auth/email/code/verify",
            new
            {
                email = administrator.EmailAddress,
                code = _factory.Email.LatestSignInCode(administrator.EmailAddress),
            });

        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The session is real: the account area is reachable, which is the
        // point of having the weaker route at all. Being locked out of one's
        // own account is not the outcome anybody wanted.
        (await weak.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await weak.PutAsJsonAsync(
            $"/api/auth/admin/users/{await IdOf(target.EmailAddress)}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Machine-readable, so the browser application can say "sign in with
        // your passkey" instead of the generic "you do not have access", which
        // would be both wrong and unactionable — the account does have access.
        (await refused.ReadJsonAsync())
            .GetProperty("code").GetString()
            .ShouldBe(Sw5eIdentityServiceCollectionExtensions.StrongAuthenticationRequired);

        // And the refusal was real rather than cosmetic: nothing was granted.
        var roles = await RolesOf(target.EmailAddress);
        roles.ShouldNotContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task TheSameAdministratorCanAdministerAfterAPasskeySignIn()
    {
        // The control. Without it, a policy that refused every administrator
        // unconditionally would pass the test above.
        var client = _factory.CreateBrowserClient();
        var administrator = await EstablishAsync(client, "elevated-admin-strong");
        await PromoteAsync(administrator.EmailAddress, Sw5eRoles.Administrator);

        var target = await EstablishAsync(_factory.CreateBrowserClient(), "elevated-target-strong");

        // Promotion rotates the security stamp, so the session opened during
        // setup is on its way out. Sign in again, with a passkey this time.
        await client.PostAsync("/api/auth/logout", content: null);
        (await administrator.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);

        var granted = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{await IdOf(target.EmailAddress)}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RolesOf(target.EmailAddress)).ShouldContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task AContributorWithoutAStrongSessionIsRefusedTheContributorPolicy()
    {
        // The Contribute policy has no endpoint behind it yet — content upload
        // is still to come — so it is exercised directly against the real
        // authorization service, with a principal built by the real claims
        // factory for a real account in the real database. That is the same
        // decision the middleware would make, reached the same way.
        var client = _factory.CreateBrowserClient();
        var contributor = await EstablishAsync(client, "elevated-contributor");
        await PromoteAsync(contributor.EmailAddress, Sw5eRoles.Contributor);

        using var scope = _factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();
        var claims = scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<Sw5eUser>>();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var user = await users.FindByEmailAsync(contributor.EmailAddress);
        user.ShouldNotBeNull();

        var principal = await claims.CreateAsync(user);

        // Exactly what a session established with an emailed code carries.
        var weak = Stamp(principal, Sw5eClaims.EmailCodeMethod);
        var strong = Stamp(principal, Sw5eClaims.PasskeyMethod);

        (await authorization.AuthorizeAsync(weak, Sw5ePolicies.Contribute))
            .Succeeded.ShouldBeFalse();

        // The same account, the same roles, one different claim. That isolates
        // the refusal to the sign-in method rather than to anything about the
        // account, which is what makes the assertion above mean something.
        (await authorization.AuthorizeAsync(strong, Sw5ePolicies.Contribute))
            .Succeeded.ShouldBeTrue();

        // And an authenticator code is the other way to satisfy it.
        (await authorization.AuthorizeAsync(
                Stamp(principal, Sw5eClaims.AuthenticatorMethod), Sw5ePolicies.Contribute))
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ACommunityAccountIsUnaffectedByTheRule()
    {
        // The rule governs publishing, not signing in. A reader managing their
        // own profile with a mailbox code must not meet any of it.
        var client = _factory.CreateBrowserClient();
        var account = await EstablishAsync(client, "elevated-community");
        await client.PostAsync("/api/auth/logout", content: null);

        var weak = _factory.CreateBrowserClient();
        await weak.PostAsJsonAsync("/api/auth/email/code", new { email = account.EmailAddress });

        await weak.PostAsJsonAsync(
            "/api/auth/email/code/verify",
            new
            {
                email = account.EmailAddress,
                code = _factory.Email.LatestSignInCode(account.EmailAddress),
            });

        var me = await weak.GetAsync("/api/auth/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await me.ReadJsonAsync();

        // Nothing is being asked of them, and the front end is told so rather
        // than left to work it out from the role list.
        body.GetProperty("secondFactorRequired").GetBoolean().ShouldBeFalse();

        // Enrolling a passkey is still available from this session — the offer
        // this flow makes to somebody who arrived without one would be empty
        // otherwise.
        (await weak.PostAsync("/api/auth/passkey/register/begin", content: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GrantingARoleToAnAccountWithNoSecondFactorSaysSoRatherThanFailingQuietly()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = await EstablishAsync(client, "elevated-granter");
        await PromoteAsync(administrator.EmailAddress, Sw5eRoles.Administrator);

        await client.PostAsync("/api/auth/logout", content: null);
        (await administrator.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);

        // An account that has proved its address and has no passkey and no
        // authenticator — the state somebody who signs in by emailed code is
        // in, and therefore the state a newly-appointed contributor may well be
        // in.
        var newcomer = AccountFlow.For(_factory.CreateBrowserClient(), "elevated-newcomer");
        await newcomer.RegisterAsync();
        await newcomer.VerifyEmailAsync(_factory.Email);

        var granted = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{await IdOf(newcomer.EmailAddress)}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

        // The grant lands. Refusing it would make the administrator's action
        // fail for a reason about somebody else's device, and would leave no
        // way to appoint a contributor who has not enrolled yet.
        granted.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await granted.ReadJsonAsync();
        body.GetProperty("roles").EnumerateArray()
            .Select(role => role.GetString())
            .ShouldContain(Sw5eRoles.Contributor);

        // And the administrator is told, on screen, that it cannot be used yet.
        body.GetProperty("awaitingSecondFactor").GetBoolean().ShouldBeTrue();

        // As is the person it was granted to, by email, with what to do about
        // it. A grant nobody can act on and nobody was told about is a support
        // ticket waiting to happen.
        var notice = _factory.Email
            .For(newcomer.EmailAddress)
            .Last(message => message.Kind == AccountMessageKind.SecurityNotice);

        notice.Body.ShouldContain("passkey");
        notice.Body.ShouldContain("authenticator");
    }

    [Fact]
    public async Task GrantingARoleToAnAccountThatHasAPasskeyIsNotFlagged()
    {
        // The control for the flag. An implementation that always reported
        // awaitingSecondFactor would pass the test above and be useless.
        var client = _factory.CreateBrowserClient();
        var administrator = await EstablishAsync(client, "elevated-granter-ok");
        await PromoteAsync(administrator.EmailAddress, Sw5eRoles.Administrator);

        await client.PostAsync("/api/auth/logout", content: null);
        (await administrator.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);

        var enrolled = await EstablishAsync(_factory.CreateBrowserClient(), "elevated-enrolled");

        var granted = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{await IdOf(enrolled.EmailAddress)}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await granted.ReadJsonAsync())
            .GetProperty("awaitingSecondFactor").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task AnElevatedAccountIsToldThatASecondFactorIsExpectedOfIt()
    {
        var client = _factory.CreateBrowserClient();
        var contributor = await EstablishAsync(client, "elevated-told");
        await PromoteAsync(contributor.EmailAddress, Sw5eRoles.Contributor);

        // Promotion rotates the stamp, so this is a fresh session.
        await client.PostAsync("/api/auth/logout", content: null);
        (await contributor.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();

        body.GetProperty("secondFactorRequired").GetBoolean().ShouldBeTrue();
        body.GetProperty("strongAuthentication").GetBoolean().ShouldBeTrue();
        body.GetProperty("authenticationMethod").GetString().ShouldBe("passkey");
    }

    /// <summary>
    /// A copy of a principal carrying one sign-in method claim.
    /// </summary>
    /// <remarks>
    /// Built by adding the claim the sign-in path adds, to a principal the real
    /// claims factory produced. Constructing an identity from scratch here
    /// would be testing a principal the application never issues.
    /// </remarks>
    private static ClaimsPrincipal Stamp(ClaimsPrincipal principal, string method)
    {
        var identity = new ClaimsIdentity(
            principal.Claims.Append(Sw5eClaims.For(method)),
            IdentityConstants.ApplicationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    private async Task<AccountFlow> EstablishAsync(HttpClient client, string label) =>
        await AccountFlow.For(client, label).EstablishAsync(_factory.Email);

    /// <summary>
    /// Grants a role by way of the store rather than the API.
    /// </summary>
    /// <remarks>
    /// The API route is the thing under test in two of the cases above, and
    /// bootstrapping the first administrator through it is impossible anyway —
    /// only an administrator can appoint one. This is the equivalent of the
    /// operator running the bootstrap setting, and it is used only to arrange,
    /// never to assert.
    /// </remarks>
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
}
