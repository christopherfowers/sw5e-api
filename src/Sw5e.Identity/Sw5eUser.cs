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

    /// <summary>
    /// When an administrator suspended this account, or null if they have not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A timestamp rather than a <see langword="bool"/>, because the question a
    /// reviewer asks about a suspension is almost never "is it on" — it is
    /// "since when", and a boolean answers that with a shrug. It also makes the
    /// column its own audit trail of last resort if the administrative log is
    /// ever unavailable.
    /// </para>
    /// <para>
    /// <b>Not the same thing as <see cref="IdentityUser{TKey}.LockoutEnd"/>,
    /// and deliberately a separate column.</b> Lockout is the automatic,
    /// self-healing consequence of failed sign-in attempts; it is set by the
    /// framework, it expires by itself, and an attacker can cause one at will
    /// against any account they can name. Suspension is a decision a person
    /// made. Storing the two in one column would mean an administrator lifting
    /// a suspension also clears somebody's guessing attack, and a lockout
    /// expiring quietly reinstates an account nobody meant to reinstate.
    /// </para>
    /// <para>
    /// What it actually does is set out in <c>AccountSuspension</c>: the short
    /// version is that a suspended account cannot obtain a session by any route
    /// and cannot use one it already had.
    /// </para>
    /// </remarks>
    public DateTimeOffset? SuspendedAt { get; set; }

    /// <summary>
    /// Why, in the administrator's own words. Null when the account is not
    /// suspended.
    /// </summary>
    /// <remarks>
    /// Written by an administrator and read by administrators. It is never sent
    /// to the account it is about: a suspension notice that quoted the reason
    /// would hand somebody the exact wording to argue with, and — where the
    /// reason is an investigation — would tell them what is being investigated.
    /// The account is told that it is suspended and who to contact, which is
    /// the part that concerns them.
    /// </remarks>
    public string? SuspensionReason { get; set; }

    /// <summary>The administrator who suspended it, or null.</summary>
    /// <remarks>
    /// Denormalised onto the account so that "who did this" is answerable from
    /// the row itself rather than only from a scan of the administrative log.
    /// The log is still the record; this is the pointer into it.
    /// </remarks>
    public Guid? SuspendedByUserId { get; set; }
}
