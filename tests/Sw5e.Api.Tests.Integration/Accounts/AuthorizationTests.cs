using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// That roles decide what an account may do, and that holding the wrong one is
/// refused rather than ignored.
/// </summary>
[Collection(AccountTestCollection.Name)]
public sealed class AuthorizationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AnAnonymousCallerCannotAssignRoles()
    {
        var client = _factory.CreateBrowserClient();
        var target = await EstablishAccountAsync("victim");

        var response = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/roles", new { roles = new[] { Sw5eRoles.Administrator } });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RolesOfAsync(target)).ShouldNotContain(Sw5eRoles.Administrator);
    }

    [Fact]
    public async Task AnOrdinaryMemberCannotAssignRoles()
    {
        var client = _factory.CreateBrowserClient();
        var member = AccountFlow.For(client, "community");
        await member.EstablishAsync(_factory.Email);

        var target = await EstablishAccountAsync("promotion-target");

        var response = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/roles", new { roles = new[] { Sw5eRoles.Contributor } });

        // Forbidden rather than unauthorized: the caller is known, and is not
        // permitted. Answering 401 would tell them to sign in again, which they
        // already have.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // And the refusal has to be real. A 403 from an endpoint that had
        // already written the role would be worse than no check at all.
        (await RolesOfAsync(target)).ShouldNotContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task AContributorStillCannotAssignRoles()
    {
        var client = _factory.CreateBrowserClient();
        var contributor = AccountFlow.For(client, "contributor");
        await contributor.EstablishAsync(_factory.Email);

        var contributorId = await IdOfAsync(contributor.EmailAddress);
        await GrantAsync(contributorId, Sw5eRoles.Contributor);

        // Re-established so the session carries the new role.
        await client.PostAsync("/api/auth/logout", content: null);
        await contributor.SignInAsync();

        (await (await client.GetAsync("/api/auth/me")).ReadJsonAsync())
            .GetProperty("roles").EnumerateArray().Select(role => role.GetString())
            .ShouldContain(Sw5eRoles.Contributor);

        var target = await EstablishAccountAsync("contributor-target");

        var response = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/roles", new { roles = new[] { Sw5eRoles.Contributor } });

        // Uploading content and handing out the ability to upload content are
        // different privileges, and the Contributor role is only the first.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RolesOfAsync(target)).ShouldNotContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task AnAdministratorCanGrantAndRevokeTheContributorRole()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = AccountFlow.For(client, "admin");
        await administrator.EstablishAsync(_factory.Email);

        await GrantAsync(await IdOfAsync(administrator.EmailAddress), Sw5eRoles.Administrator);
        await client.PostAsync("/api/auth/logout", content: null);
        await administrator.SignInAsync();

        var target = await EstablishAccountAsync("granted");

        var granted = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/roles", new { roles = new[] { Sw5eRoles.Contributor } });

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOfAsync(target)).ShouldContain(Sw5eRoles.Contributor);

        // An empty list is a declaration that the account should hold no
        // assignable role, so the grant is withdrawn rather than accumulated.
        var revoked = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/roles", new { roles = Array.Empty<string>() });

        revoked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOfAsync(target)).ShouldNotContain(Sw5eRoles.Contributor);

        // Community survives, because it is the floor every account stands on
        // rather than something an administrator hands out.
        (await RolesOfAsync(target)).ShouldContain(Sw5eRoles.Community);
    }

    [Fact]
    public async Task AnAdministratorCannotRemoveTheirOwnAdministratorRole()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = AccountFlow.For(client, "self-demote");
        await administrator.EstablishAsync(_factory.Email);

        var id = await IdOfAsync(administrator.EmailAddress);
        await GrantAsync(id, Sw5eRoles.Administrator);
        await client.PostAsync("/api/auth/logout", content: null);
        await administrator.SignInAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{id}/roles", new { roles = Array.Empty<string>() });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RolesOfAsync(id)).ShouldContain(Sw5eRoles.Administrator);
    }

    [Fact]
    public async Task AnUnknownRoleNameIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = AccountFlow.For(client, "bad-role");
        await administrator.EstablishAsync(_factory.Email);

        await GrantAsync(await IdOfAsync(administrator.EmailAddress), Sw5eRoles.Administrator);
        await client.PostAsync("/api/auth/logout", content: null);
        await administrator.SignInAsync();

        var target = await EstablishAccountAsync("bad-role-target");

        var response = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{target}/roles", new { roles = new[] { "Superuser" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Establishes a fully signed-up account on its own client and returns its
    /// identifier.
    /// </summary>
    private async Task<Guid> EstablishAccountAsync(string label)
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, label);

        await account.EstablishAsync(_factory.Email);

        return await IdOfAsync(account.EmailAddress);
    }

    /// <summary>
    /// Grants a role directly through the store.
    /// </summary>
    /// <remarks>
    /// This stands in for the bootstrap promotion, which is the only way the
    /// first administrator ever comes into being — there is no endpoint that
    /// creates one, by design, because an endpoint that could would be the most
    /// attractive target on the platform.
    /// </remarks>
    private async Task GrantAsync(Guid userId, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("The account to promote does not exist.");

        var result = await users.AddToRoleAsync(user, role);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }

    private async Task<Guid> IdOfAsync(string emailAddress)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress)
            ?? throw new InvalidOperationException($"No account exists for {emailAddress}.");

        return user.Id;
    }

    private async Task<IList<string>> RolesOfAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("The account does not exist.");

        return await users.GetRolesAsync(user);
    }
}
