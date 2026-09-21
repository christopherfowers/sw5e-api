using System.Globalization;
using System.Security.Claims;

namespace Sw5e.Identity;

/// <summary>
/// The claims this platform adds to a session beyond the ones ASP.NET Core
/// Identity writes for itself.
/// </summary>
/// <remarks>
/// Two claims. <see cref="AuthenticationMethod"/> records how the session was
/// established, because the platform issues credentials of two very different
/// strengths. <see cref="AuthenticatedAt"/> records when, because for a
/// handful of actions "at some point today" is not a good enough answer.
/// </remarks>
public static class Sw5eClaims
{
    /// <summary>
    /// How the session was established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before email sign-in codes existed there was exactly one way in, so
    /// "signed in" and "signed in strongly" were the same statement and no
    /// claim was needed to tell them apart. They are not the same statement any
    /// more: a six-digit code sent to a mailbox is a far weaker proof of
    /// identity than a passkey assertion with user verification, and the whole
    /// point of adding it was to admit people whose device cannot do the
    /// stronger thing.
    /// </para>
    /// <para>
    /// This claim records which it was, on the session itself, at the moment
    /// the session was created. That placement is the important part. The
    /// obvious alternative, deciding at request time whether the account
    /// <em>has</em> a passkey or an authenticator, answers a different and
    /// much weaker question: it says the account could have proved something
    /// strongly, not that it did. An administrator with a passkey who signed in
    /// with a mailbox code would pass that check, which means a compromised
    /// mailbox would be a complete administrative takeover despite the passkey.
    /// </para>
    /// <para>
    /// Because it is written into the ticket rather than read from the store,
    /// it also cannot go stale in the dangerous direction: enrolling a passkey
    /// after signing in weakly does not retroactively strengthen the session
    /// that is already open, and there is no window in which a cached value
    /// grants more than it should.
    /// </para>
    /// </remarks>
    public const string AuthenticationMethod = "sw5e:amr";

    /// <summary>A WebAuthn assertion with user verification.</summary>
    public const string PasskeyMethod = "passkey";

    /// <summary>A code from an enrolled authenticator app.</summary>
    public const string AuthenticatorMethod = "totp";

    /// <summary>A one-time code sent to the account's email address.</summary>
    public const string EmailCodeMethod = "email";

    /// <summary>
    /// The methods that count as a second factor for an elevated role.
    /// </summary>
    /// <remarks>
    /// Possession of a device, in both cases, and possession that the person
    /// had to demonstrate during this sign-in. An email code is possession of a
    /// mailbox, which is the thing everything else on the internet already
    /// recovers through, so it can never be the factor that protects a
    /// privilege.
    /// </remarks>
    private static readonly string[] StrongMethods = [PasskeyMethod, AuthenticatorMethod];

    /// <summary>
    /// Whether this principal proved something stronger than mailbox control.
    /// </summary>
    public static bool HasStrongAuthentication(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        foreach (var method in StrongMethods)
        {
            if (principal.HasClaim(AuthenticationMethod, method))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the claim for one method.</summary>
    public static Claim For(string method) => new(AuthenticationMethod, method);

    /// <summary>
    /// When the session last proved a factor, as Unix seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AuthenticationMethod"/> answers what was proved and this
    /// answers when, and the two are only useful together. A session lasts a
    /// working day, so a passkey demonstrated at nine in the morning is still
    /// the reason a request at five in the afternoon is allowed. That is the
    /// right answer for publishing a rules page and the wrong one for handing
    /// somebody the Administrator role, because in between sits the unattended
    /// laptop, and the whole value of the passkey is that the person holding it
    /// was present.
    /// </para>
    /// <para>
    /// So the privileged handful of actions ask for the factor again, and this
    /// is the field that lets the server tell the difference between a proof
    /// that happened a minute ago and one that happened this morning. See
    /// <see cref="Authorization.RecentAuthenticationRequirement"/>.
    /// </para>
    /// <para>
    /// Seconds since the epoch rather than a formatted date, and the name is
    /// the one OpenID Connect gives the same idea, because a claim value is a
    /// string either way and this is the spelling that cannot be misread by
    /// anything reading a culture out of the ambient thread.
    /// </para>
    /// </remarks>
    public const string AuthenticatedAt = "sw5e:auth_time";

    /// <summary>Builds the claim for one instant.</summary>
    public static Claim At(DateTimeOffset when) => new(
        AuthenticatedAt,
        when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        ClaimValueTypes.Integer64);

    /// <summary>
    /// When this session last proved a factor, or null if it does not say.
    /// </summary>
    /// <remarks>
    /// Null for a session issued before the claim existed, and for a value that
    /// does not parse. Both are treated the same way everywhere that reads
    /// this: as "not recently", which is the answer that asks somebody to
    /// confirm rather than the one that lets an unreadable session through.
    /// </remarks>
    public static DateTimeOffset? AuthenticatedAtOf(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirst(AuthenticatedAt)?.Value;

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>
    /// Whether this principal proved a second factor inside <paramref name="window"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Strength as well as freshness, deliberately. A recently emailed code is
    /// recent and is still a mailbox, so checking the timestamp alone would
    /// hand the privileged actions to exactly the credential the rest of this
    /// file exists to keep away from them. Callers compose this with the
    /// policies rather than relying on them, so a requirement attached to the
    /// wrong policy fails closed.
    /// </para>
    /// <para>
    /// The comparison is on the absolute difference. A timestamp in the future
    /// cannot have been written by this server against this clock, so it is not
    /// evidence of anything and is refused; allowing a window's worth of it
    /// absorbs the small disagreement between two replicas without turning a
    /// clock that jumped backwards into a standing grant.
    /// </para>
    /// </remarks>
    public static bool HasRecentStrongAuthentication(
        ClaimsPrincipal principal,
        TimeSpan window,
        DateTimeOffset now)
    {
        if (!HasStrongAuthentication(principal))
        {
            return false;
        }

        return AuthenticatedAtOf(principal) is { } proved
            && (now - proved).Duration() <= window;
    }
}
