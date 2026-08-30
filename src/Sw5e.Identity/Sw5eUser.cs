using Microsoft.AspNetCore.Identity;

namespace Sw5e.Identity;

/// <summary>
/// A platform account.
/// </summary>
/// <remarks>
/// <para>
/// Deriving from <see cref="IdentityUser{TKey}"/> rather than modelling an
/// account by hand is the whole point: every field an attacker cares about —
/// the security stamp, the lockout counters, the normalized identifiers used
/// for uniqueness — is maintained by the framework, on the framework's
/// schedule, and none of it can be forgotten here by accident.
/// </para>
/// <para>
/// The key is a <see cref="Guid"/>, not an <see cref="int"/>. Account
/// identifiers end up in URLs and in administrative payloads, and a sequential
/// integer would publish both the size of the user base and the order people
/// joined in. It also removes any temptation to guess a neighbouring account.
/// </para>
/// <para>
/// <see cref="IdentityUser{TKey}.PasswordHash"/> is inherited and stays null
/// for every account this platform creates. There is no registration path that
/// sets a password, no endpoint that checks one, and no password validator
/// registered. Passkeys are the credential; the column exists only because the
/// framework's schema defines it.
/// </para>
/// </remarks>
public sealed class Sw5eUser : IdentityUser<Guid>
{
    /// <summary>
    /// The name shown beside the account's contributions.
    /// </summary>
    /// <remarks>
    /// Held separately from <see cref="IdentityUser{TKey}.UserName"/>, which is
    /// the email address and is never displayed. Publishing the username of an
    /// account whose username is its email address is a data leak dressed up as
    /// a feature.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When the account was first created, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
