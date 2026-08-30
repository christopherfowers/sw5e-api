using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// The three short-lived pieces of state the account flows carry between one
/// request and the next: an in-flight passkey registration challenge, an
/// in-flight passkey sign-in challenge, and permission to enrol a first passkey
/// after proving control of an email address.
/// </summary>
/// <remarks>
/// <para>
/// All three are held in cookies, and all three are encrypted and signed with
/// ASP.NET Core data protection before they get there. The cookie is a
/// transport, not a trust boundary: nothing read back out of one is believed
/// until the payload has been unprotected, which fails closed on tampering, on
/// an unexpected purpose, and on expiry.
/// </para>
/// <para>
/// Each purpose string is distinct and versioned. Data protection derives a
/// different key per purpose, so a value minted for one of these flows cannot
/// be replayed into another even though all three travel in cookies on the same
/// origin. That is what stops a passkey login challenge being presented as
/// permission to enrol a credential.
/// </para>
/// <para>
/// Expiry is enforced twice over. The cookie carries a <c>Max-Age</c>, which is
/// a request to the browser and nothing more — a caller who keeps the value can
/// send it forever. The payload is additionally protected with a time-limited
/// protector, and that one is checked by the server on every read, so a
/// preserved cookie is worthless the moment its window closes.
/// </para>
/// <para>
/// SignInManager offers to manage passkey challenge state itself, and this code
/// deliberately does not use that path. It stores the challenge in the
/// two-factor cookie slot, which this application needs for its actual purpose:
/// carrying a half-completed sign-in between the passkey step and the TOTP
/// step. Keeping the two apart means neither flow can clear the other's state.
/// </para>
/// </remarks>
internal sealed class AccountStateCookies(IDataProtectionProvider dataProtection)
{
    /// <summary>Carries the WebAuthn challenge for an in-flight enrolment.</summary>
    public const string RegistrationChallengeCookie = "__Host-sw5e.pk-register";

    /// <summary>Carries the WebAuthn challenge for an in-flight sign-in.</summary>
    public const string LoginChallengeCookie = "__Host-sw5e.pk-login";

    /// <summary>Carries permission to enrol a first passkey.</summary>
    public const string EnrollmentCookie = "__Host-sw5e.enrol";

    /// <summary>
    /// How long a WebAuthn challenge stays answerable.
    /// </summary>
    /// <remarks>
    /// The authenticator's own timeout is two minutes; this is the server's
    /// outer bound on the same exchange, generous enough to absorb a slow
    /// biometric prompt and short enough that an abandoned challenge is not
    /// left standing.
    /// </remarks>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after verifying an email address a first passkey may be
    /// enrolled.
    /// </summary>
    /// <remarks>
    /// This window is the most dangerous thing in the account flows: within it,
    /// possession of the emailed link is enough to attach a new credential to
    /// the account. Ten minutes is enough to open a link, be prompted by the
    /// authenticator and finish, and it is not enough to be worth stealing a
    /// mailbox backup for.
    /// </remarks>
    public static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _registrationChallenge =
        Protector(dataProtection, "Sw5e.Accounts.PasskeyAttestationState.v1");

    private readonly ITimeLimitedDataProtector _loginChallenge =
        Protector(dataProtection, "Sw5e.Accounts.PasskeyAssertionState.v1");

    private readonly ITimeLimitedDataProtector _enrollment =
        Protector(dataProtection, "Sw5e.Accounts.PasskeyEnrollmentTicket.v1");

    public void StoreRegistrationChallenge(HttpContext context, string state) =>
        Write(context, RegistrationChallengeCookie, _registrationChallenge, state, ChallengeLifetime);

    public string? ReadRegistrationChallenge(HttpContext context) =>
        Read(context, RegistrationChallengeCookie, _registrationChallenge);

    public void StoreLoginChallenge(HttpContext context, string state) =>
        Write(context, LoginChallengeCookie, _loginChallenge, state, ChallengeLifetime);

    public string? ReadLoginChallenge(HttpContext context) =>
        Read(context, LoginChallengeCookie, _loginChallenge);

    /// <summary>
    /// Grants the bearer permission to enrol a passkey on one account.
    /// </summary>
    /// <remarks>
    /// The payload binds the ticket to the account's security stamp as well as
    /// its identifier. The stamp changes whenever anything security-relevant
    /// about the account does — including every time a fresh recovery link is
    /// issued — so an older outstanding ticket stops working the moment a newer
    /// one is minted. Without that binding, every recovery email ever sent for
    /// an account would remain a live key to it until it expired.
    /// </remarks>
    public void StoreEnrollmentTicket(HttpContext context, Guid userId, string securityStamp) =>
        Write(
            context,
            EnrollmentCookie,
            _enrollment,
            $"{userId:N}:{securityStamp}",
            EnrollmentLifetime);

    /// <summary>
    /// Reads back an enrolment ticket, or null if there is none, it has been
    /// tampered with, or it has expired.
    /// </summary>
    public (Guid UserId, string SecurityStamp)? ReadEnrollmentTicket(HttpContext context)
    {
        var payload = Read(context, EnrollmentCookie, _enrollment);

        if (payload is null)
        {
            return null;
        }

        var separator = payload.IndexOf(':', StringComparison.Ordinal);

        if (separator <= 0 || !Guid.TryParseExact(payload[..separator], "N", out var userId))
        {
            return null;
        }

        return (userId, payload[(separator + 1)..]);
    }

    /// <summary>
    /// Removes a piece of state. Called on the success and the failure path
    /// alike: a challenge that has been answered once must not be answerable
    /// again, and a challenge that failed must not be retried against a
    /// different credential.
    /// </summary>
    public static void Clear(HttpContext context, string name) =>
        context.Response.Cookies.Delete(name, DeletionOptions);

    private static void Write(
        HttpContext context,
        string name,
        ITimeLimitedDataProtector protector,
        string value,
        TimeSpan lifetime) =>
        context.Response.Cookies.Append(
            name,
            protector.Protect(value, lifetime),
            new CookieOptions
            {
                // Same reasoning as the session cookie, and for the same
                // reasons: unreachable from script, never sent in the clear,
                // never attached to a cross-site request, and eligible for the
                // __Host- prefix the names above rely on.
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
                MaxAge = lifetime,
            });

    private static string? Read(HttpContext context, string name, ITimeLimitedDataProtector protector)
    {
        if (!context.Request.Cookies.TryGetValue(name, out var protectedValue) ||
            string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            // Covers every way a value can fail to be ours: expired, truncated,
            // re-signed with a key we do not hold, or minted for a different
            // purpose. All of them are the same answer — there is no valid
            // state here — and none of them is worth distinguishing to the
            // caller, who is at best holding a stale cookie and at worst
            // probing.
            return null;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider provider, string purpose) =>
        provider.CreateProtector(purpose).ToTimeLimitedDataProtector();

    private static CookieOptions DeletionOptions => new()
    {
        // A deletion only lands if its attributes match the cookie being
        // deleted. Get the path wrong and the browser keeps the original
        // happily, leaving a spent challenge in place.
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
