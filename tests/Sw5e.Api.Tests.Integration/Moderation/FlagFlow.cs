using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Identity;
using Sw5e.Infrastructure.Persistence.Moderation;

namespace Sw5e.Api.Tests.Integration.Moderation;

/// <summary>
/// Setup the flag tests share: signed-in accounts of each role, and a way to
/// look at the store directly.
/// </summary>
/// <remarks>
/// <para>
/// Reading the table is the point of the second half. Almost every test in this
/// area is about something <em>not</em> happening, and a refusal is only worth
/// asserting alongside the absence of the row it refused — a 403 from an
/// endpoint that has already written is theatre, and a status-code check on its
/// own cannot tell the two apart.
/// </para>
/// <para>
/// Nothing here fabricates a session. Accounts are established by posting to
/// the real endpoints, exactly as <see cref="AccountFlow"/> does, because a
/// fixture that reached into the container to mint a Contributor cookie would
/// leave the authorization path these tests exist to check untested.
/// </para>
/// </remarks>
internal static class FlagFlow
{
    /// <summary>
    /// A document the committed content fixture actually holds. Reports have to
    /// point at something real, so the tests point at the same thing the
    /// content endpoint tests do.
    /// </summary>
    public const string DocumentType = "species";

    public const string DocumentKey = "wookiee";

    /// <summary>
    /// The attribution record for the Wookiee portrait, which is how a picture
    /// is addressed: content type <c>asset-credit</c>, key <c>{group}-{key}</c>.
    /// </summary>
    public const string ImageType = "asset-credit";

    public const string ImageKey = "species-wookiee";

    /// <summary>Registers, verifies, enrols a passkey and signs in.</summary>
    public static async Task<AccountFlow> SignInAsync(
        AccountApiFactory factory,
        HttpClient client,
        string label)
    {
        var account = AccountFlow.For(client, label);
        await account.EstablishAsync(factory.Email);
        return account;
    }

    /// <summary>
    /// Establishes an account, grants it a role, and signs it back in so the
    /// session carries the role.
    /// </summary>
    /// <remarks>
    /// The second sign-in is not optional. Role claims are written into the
    /// cookie when the session is created, so a grant applied to an open
    /// session does not reach it — which is the behaviour the platform wants
    /// and would make this fixture silently produce a Community session under a
    /// Contributor's name.
    /// </remarks>
    public static async Task<AccountFlow> SignInWithRoleAsync(
        AccountApiFactory factory,
        HttpClient client,
        string label,
        string role)
    {
        var account = await SignInAsync(factory, client, label);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

            var user = await users.FindByEmailAsync(account.EmailAddress)
                ?? throw new InvalidOperationException("The account was not created.");

            var result = await users.AddToRoleAsync(user, role);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Join("; ", result.Errors.Select(error => error.Description)));
            }
        }

        await client.PostAsync("/api/auth/logout", content: null);

        var signIn = await account.SignInAsync();

        if (signIn.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Could not re-establish the {role} session: {signIn.StatusCode}.");
        }

        return account;
    }

    /// <summary>Files a report and returns the response, asserting nothing.</summary>
    public static Task<HttpResponseMessage> RaiseAsync(
        HttpClient client,
        string reason,
        string targetType = DocumentType,
        string targetKey = DocumentKey,
        string? details = null) =>
        client.PostAsJsonAsync(
            "/api/flags",
            new { reason, targetType, targetKey, details });

    /// <summary>Files a report and insists it was accepted.</summary>
    public static async Task<Guid> RaiseAcceptedAsync(
        HttpClient client,
        string reason,
        string targetType = DocumentType,
        string targetKey = DocumentKey,
        string? details = null)
    {
        var response = await RaiseAsync(client, reason, targetType, targetKey, details);

        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Filing a report was expected to succeed but answered {(int)response.StatusCode}: " +
                await response.Content.ReadAsStringAsync());
        }

        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Empties the flag table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every test class in this collection shares one PostgreSQL container, and
    /// nothing resets it between them. The account tests get away with that by
    /// giving every account an address nothing else will use; the flag tests
    /// cannot, because most of what they assert is that a table is empty or
    /// holds exactly one row, and a leftover from the previous class would make
    /// those pass or fail for reasons that have nothing to do with the test.
    /// </para>
    /// <para>
    /// Safe because a collection's classes never run in parallel, so no other
    /// test is looking at this table while it is being emptied.
    /// </para>
    /// </remarks>
    public static async Task ClearAsync(AccountApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<Sw5eModerationDbContext>();

        await store.ContentFlags.ExecuteDeleteAsync();
    }

    /// <summary>Every report in the store, read straight from the table.</summary>
    public static async Task<IReadOnlyList<ContentFlagRow>> StoredAsync(AccountApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<Sw5eModerationDbContext>();

        return await store.ContentFlags.AsNoTracking().ToListAsync();
    }

    /// <summary>How many reports one account has filed.</summary>
    public static async Task<int> StoredCountAsync(AccountApiFactory factory, string emailAddress)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();
        var store = scope.ServiceProvider.GetRequiredService<Sw5eModerationDbContext>();

        var user = await users.FindByEmailAsync(emailAddress)
            ?? throw new InvalidOperationException($"No account exists for {emailAddress}.");

        return await store.ContentFlags.CountAsync(flag => flag.ReporterUserId == user.Id);
    }

    /// <summary>One stored report, by identifier.</summary>
    public static async Task<ContentFlagRow> StoredAsync(AccountApiFactory factory, Guid flagId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<Sw5eModerationDbContext>();

        return await store.ContentFlags.AsNoTracking().SingleAsync(flag => flag.Id == flagId);
    }

    /// <summary>The array a list response carries, as JSON elements.</summary>
    public static JsonElement[] FlagsIn(JsonElement body) =>
        [.. body.GetProperty("flags").EnumerateArray()];
}
