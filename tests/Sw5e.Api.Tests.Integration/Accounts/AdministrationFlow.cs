using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Setup the administrative tests share.
/// </summary>
/// <remarks>
/// <para>
/// The one shortcut taken here is the very first role grant, and it has to be:
/// only an administrator can appoint an administrator, so there is no path
/// through the API from an empty database to a first one. That is what the
/// bootstrap setting exists for in a deployment, and this stands in for it.
/// It is used to arrange and never to assert — every test below that cares
/// whether a grant works goes through the endpoint.
/// </para>
/// <para>
/// Everything else is the real thing. Accounts register, verify a real emailed
/// token, enrol a passkey through a real WebAuthn ceremony and sign in with a
/// real assertion, because a fixture that fabricated a session would leave the
/// authorization path these tests exist to check untested.
/// </para>
/// </remarks>
internal static class AdministrationFlow
{
    /// <summary>
    /// An account holding a role, signed in with a passkey.
    /// </summary>
    /// <remarks>
    /// The second sign-in is not optional. Role claims are written into the
    /// cookie when the session is created, so a grant applied to an open
    /// session does not reach it — and the role grant rotates the security
    /// stamp anyway, which is on its way to ending that session regardless.
    /// </remarks>
    public static async Task<AccountFlow> SignInWithRoleAsync(
        AccountApiFactory factory,
        HttpClient client,
        string label,
        string role)
    {
        var account = AccountFlow.For(client, label);
        await account.EstablishAsync(factory.Email);

        await GrantAsync(factory, account.EmailAddress, role);

        await client.PostAsync("/api/auth/logout", content: null);

        var signIn = await account.SignInAsync();

        if (signIn.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Could not re-establish the {role} session: {signIn.StatusCode}.");
        }

        return account;
    }

    /// <summary>An administrator signed in with a passkey.</summary>
    public static Task<AccountFlow> AdministratorAsync(
        AccountApiFactory factory,
        HttpClient client,
        string label) =>
        SignInWithRoleAsync(factory, client, label, Sw5eRoles.Administrator);

    /// <summary>An ordinary account, signed in with a passkey.</summary>
    public static async Task<AccountFlow> MemberAsync(
        AccountApiFactory factory,
        HttpClient client,
        string label)
    {
        var account = AccountFlow.For(client, label);
        await account.EstablishAsync(factory.Email);
        return account;
    }

    /// <summary>
    /// Signs an account in the weak way: a code emailed to its address.
    /// </summary>
    /// <remarks>
    /// This is the session the administrative surface has to refuse. It is a
    /// real session — the account area is reachable from it — established by
    /// proving control of a mailbox and nothing more, which is the thing every
    /// other account on the internet is recovered through.
    /// </remarks>
    public static async Task SignInWithEmailedCodeAsync(
        AccountApiFactory factory,
        HttpClient client,
        string emailAddress)
    {
        await client.PostAsJsonAsync("/api/auth/email/code", new { email = emailAddress });

        var response = await client.PostAsJsonAsync(
            "/api/auth/email/code/verify",
            new { email = emailAddress, code = factory.Email.LatestSignInCode(emailAddress) });

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Signing in with an emailed code answered {(int)response.StatusCode}.");
        }
    }

    public static async Task GrantAsync(
        AccountApiFactory factory,
        string emailAddress,
        string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress)
            ?? throw new InvalidOperationException($"No account exists for {emailAddress}.");

        var result = await users.AddToRoleAsync(user, role);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }

    public static async Task<Guid> IdOfAsync(AccountApiFactory factory, string emailAddress)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress)
            ?? throw new InvalidOperationException($"No account exists for {emailAddress}.");

        return user.Id;
    }

    /// <summary>Whether an account still exists, read straight from the store.</summary>
    public static async Task<bool> ExistsAsync(AccountApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        return await users.FindByIdAsync(userId.ToString()) is not null;
    }

    /// <summary>
    /// Whether an account is suspended, read straight from the store rather
    /// than from the endpoint that set it.
    /// </summary>
    /// <remarks>
    /// A refusal is only worth asserting alongside the absence of the row it
    /// refused, and a success is only worth asserting alongside the presence of
    /// one. An endpoint that answered 200 and wrote nothing would satisfy every
    /// status-code check in this suite.
    /// </remarks>
    public static async Task<bool> IsSuspendedAsync(AccountApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByIdAsync(userId.ToString());

        return user?.SuspendedAt is not null;
    }
}
