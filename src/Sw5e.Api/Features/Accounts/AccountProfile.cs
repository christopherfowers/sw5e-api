using System.Buffers.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Turns an account into the view of itself that the account holder is allowed
/// to see.
/// </summary>
/// <remarks>
/// <para>
/// One place, used by every endpoint that returns an account, so that the
/// decision about what is and is not disclosed is made once. The alternative —
/// each handler projecting its own — is how a security stamp, a lockout end
/// date or a normalised email ends up in a response because one handler was
/// written in a hurry.
/// </para>
/// <para>
/// What is deliberately absent: the security stamp and the concurrency stamp
/// (internal values whose disclosure helps an attacker reason about token
/// validity), the lockout state (which tells a thief holding a session whether
/// their guessing elsewhere is working), and the password hash column.
/// </para>
/// <para>
/// The passkey list is present, and used to be a bare count. Revocation is what
/// changed the calculation: naming a credential to remove requires its
/// identifier, and an account that can only add credentials cannot cut off a
/// lost device. The identifiers are not secrets — the browser and the
/// authenticator hold them already, and they are disclosed here only to the
/// account holder — whereas the public key and the signature counter stay out,
/// because nothing in this contract needs them.
/// </para>
/// <para>
/// Two fields describe the session rather than the account:
/// <c>authenticationMethod</c> and <c>strongAuthentication</c>. They are here
/// because the browser application has to be able to explain a 403 that a
/// different sign-in would have avoided, and guessing at the reason from the
/// account's enrolments would get it wrong for exactly the case that matters —
/// somebody who has a passkey but did not use it this time.
/// </para>
/// </remarks>
internal static class AccountProfile
{
    /// <summary>
    /// Describes an account to itself, in the context of one session.
    /// </summary>
    /// <param name="method">
    /// How the caller signed in, or null when that is not known — which happens
    /// only for a session established before this field existed.
    /// </param>
    public static async Task<CurrentUserResponse> DescribeAsync(
        UserManager<Sw5eUser> users,
        Sw5eUser user,
        string? method)
    {
        var roles = await users.GetRolesAsync(user);
        var passkeys = await users.GetPasskeysAsync(user);

        var strong = method is Sw5eClaims.PasskeyMethod or Sw5eClaims.AuthenticatorMethod;

        // Whether this account's roles oblige it to hold a second factor. Not
        // whether it holds one — the front end can see the passkey list and the
        // two-factor flag and work that out — but whether the obligation
        // applies at all, so that a Community account is never shown a warning
        // about a rule that does not govern it.
        var elevated = roles.Any(role =>
            role is Sw5eRoles.Contributor or Sw5eRoles.Administrator);

        return new CurrentUserResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            [.. roles.OrderBy(role => role, StringComparer.Ordinal)],
            user.TwoFactorEnabled,
            // Oldest first, so the list a reader sees does not reshuffle
            // between requests and the credential they are about to revoke does
            // not move under the cursor.
            [.. passkeys
                .OrderBy(passkey => passkey.CreatedAt)
                .Select(passkey => new PasskeySummary(
                    Base64Url.EncodeToString(passkey.CredentialId),
                    passkey.Name,
                    passkey.CreatedAt))],
            method,
            strong,
            elevated);
    }

    /// <summary>
    /// Describes an account to a caller whose session already exists, reading
    /// the sign-in method off the principal that request arrived with.
    /// </summary>
    public static Task<CurrentUserResponse> DescribeAsync(
        UserManager<Sw5eUser> users,
        Sw5eUser user,
        ClaimsPrincipal principal) =>
        DescribeAsync(users, user, principal.FindFirstValue(Sw5eClaims.AuthenticationMethod));

    /// <summary>
    /// The body of a successful sign-in response.
    /// </summary>
    /// <remarks>
    /// Takes the method as an argument rather than reading it off
    /// <c>HttpContext.User</c>, because at the moment a sign-in completes the
    /// cookie has been written to the response and the request's own principal
    /// is still the anonymous one it arrived as. Reading it there would report
    /// every fresh sign-in as unknown.
    /// </remarks>
    public static async Task<SignInResponse> DescribeSignInAsync(
        UserManager<Sw5eUser> users,
        Sw5eUser user,
        string method) =>
        SignInResponse.Authenticated(await DescribeAsync(users, user, method));
}
