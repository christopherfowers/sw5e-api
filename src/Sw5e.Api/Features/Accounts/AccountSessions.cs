using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// The two ways a sign-in ends: a session, or a half-finished sign-in waiting
/// on a second factor.
/// </summary>
/// <remarks>
/// <para>
/// Collected here because there are now three routes in — a passkey assertion,
/// an emailed code, and either of those followed by an authenticator code — and
/// the property that matters most about them is one they must all share. Every
/// session this platform issues records <em>how</em> it was established, and a
/// route that forgot to would produce a session that silently cannot use an
/// elevated role, which is a bug that would look exactly like a permissions
/// problem and be debugged as one.
/// </para>
/// <para>
/// One method, called by every route, is the cheapest way to make forgetting
/// impossible.
/// </para>
/// </remarks>
internal static class AccountSessions
{
    /// <summary>
    /// Issues the session cookie, stamped with the method that earned it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>isPersistent</c> is false, always. A session that outlives the browser
    /// is a session that outlives the person walking away from the machine, and
    /// this platform has nothing so tedious to sign into that it is worth it.
    /// The sliding eight-hour window covers a working day.
    /// </para>
    /// <para>
    /// The claim goes into the ticket rather than into the store. See
    /// <see cref="Sw5eClaims.AuthenticationMethod"/> for why it has to be a
    /// property of this sign-in rather than of the account.
    /// </para>
    /// </remarks>
    public static Task SignInAsync(SignInManager<Sw5eUser> signIn, Sw5eUser user, string method) =>
        signIn.SignInWithClaimsAsync(user, isPersistent: false, [Sw5eClaims.For(method)]);

    /// <summary>
    /// Records that an account has passed its first factor and is waiting on
    /// its second.
    /// </summary>
    /// <remarks>
    /// The shape of this principal is not arbitrary: it is what
    /// <c>SignInManager.TwoFactorAuthenticatorSignInAsync</c> reads back, so
    /// the scheme and the claim type have to match the framework's exactly for
    /// the second half of the sign-in to find it. It carries the account
    /// identifier and nothing else — no roles, no name — because it is not an
    /// identity yet, and anything authorization could act on has no business
    /// being in it.
    /// </remarks>
    public static async Task StorePendingTwoFactorAsync(
        HttpContext context,
        UserManager<Sw5eUser> users,
        Sw5eUser user)
    {
        var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, await users.GetUserIdAsync(user)));

        await context.SignInAsync(
            IdentityConstants.TwoFactorUserIdScheme,
            new ClaimsPrincipal(identity));
    }
}
