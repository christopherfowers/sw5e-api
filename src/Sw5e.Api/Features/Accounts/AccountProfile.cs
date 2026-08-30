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
/// their guessing elsewhere is working), the password hash column, and the
/// passkey credential identifiers themselves. The passkey <em>count</em> is
/// included because the account holder needs to know whether they have a second
/// device enrolled; the identifiers are not, because nothing in this contract
/// needs them.
/// </para>
/// </remarks>
internal static class AccountProfile
{
    public static async Task<CurrentUserResponse> DescribeAsync(
        UserManager<Sw5eUser> users,
        Sw5eUser user)
    {
        var roles = await users.GetRolesAsync(user);
        var passkeys = await users.GetPasskeysAsync(user);

        return new CurrentUserResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            [.. roles.OrderBy(role => role, StringComparer.Ordinal)],
            user.TwoFactorEnabled,
            passkeys.Count);
    }
}
