using System.Buffers.Text;
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
            // Oldest first, so the list a reader sees does not reshuffle
            // between requests and the credential they are about to revoke does
            // not move under the cursor.
            [.. passkeys
                .OrderBy(passkey => passkey.CreatedAt)
                .Select(passkey => new PasskeySummary(
                    Base64Url.EncodeToString(passkey.CredentialId),
                    passkey.Name,
                    passkey.CreatedAt))]);
    }
}
