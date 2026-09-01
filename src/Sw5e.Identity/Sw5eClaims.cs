using System.Security.Claims;

namespace Sw5e.Identity;

/// <summary>
/// The claims this platform adds to a session beyond the ones ASP.NET Core
/// Identity writes for itself.
/// </summary>
/// <remarks>
/// One claim, and it exists because the platform now issues credentials of two
/// very different strengths. See <see cref="AuthenticationMethod"/>.
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
    /// obvious alternative — deciding at request time whether the account
    /// <em>has</em> a passkey or an authenticator — answers a different and
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
}
