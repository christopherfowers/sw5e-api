using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sw5e.Identity.Administration;

/// <summary>
/// What it means for an account to be suspended, and the two places that make
/// it true.
/// </summary>
/// <remarks>
/// <para>
/// A suspension that only stopped new sign-ins would be theatre. The person it
/// is aimed at is, by definition, somebody who is doing something right now,
/// and "right now" is exactly when they already have a session cookie in a tab
/// and a passkey in their pocket. So suspension is enforced twice, at the two
/// points where a request becomes an identity:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>No new session.</b> <see cref="SuspensionAwareUserConfirmation"/> makes
/// <c>SignInManager.CanSignInAsync</c> answer false, which every sign-in route
/// on the platform already consults through <c>PreSignInCheck</c> — the passkey
/// assertion, the emailed code, and the authenticator step that follows either.
/// Putting the rule there rather than in three handlers means a fourth route
/// added later inherits it instead of forgetting it.
/// </description></item>
/// <item><description>
/// <b>No use of an old one.</b> <see cref="RejectSuspendedAsync"/> runs on
/// every authenticated request, alongside the security stamp check, and signs
/// the caller out the moment it finds a suspension. Suspending also rotates the
/// security stamp, which would drop the session on its own — but only at the
/// stamp validator's next interval, up to five minutes later. Five minutes is a
/// defensible ceiling for a role revocation and an indefensible one for
/// somebody who is being removed because of what they are doing with the
/// session they are holding.
/// </description></item>
/// </list>
/// <para>
/// <b>Passkeys are left on the account.</b> They are inert while the suspension
/// stands — a valid assertion is refused at the check above, with the same
/// unhelpful 401 every other sign-in failure gets — and revoking them would
/// make reinstatement a re-credentialling exercise, turning a reversible
/// decision into an irreversible one. Suspension is meant to be reversible;
/// deletion is the door that does not open again.
/// </para>
/// <para>
/// <b>The account is never told why.</b> It is told that it is suspended, by
/// email, and told who to write to. The administrator's reason is written for
/// other administrators, and where the reason is an investigation, quoting it
/// would be telling the subject what is being investigated.
/// </para>
/// </remarks>
public static class AccountSuspension
{
    /// <summary>
    /// Longest reason an administrator may record.
    /// </summary>
    /// <remarks>
    /// Long enough for a sentence naming what happened and where the evidence
    /// is, short enough that the column is not somewhere a case file
    /// accumulates. The bound is in the column as well as in the endpoint: an
    /// endpoint check is a check, a column length is a constraint, and only one
    /// of them survives somebody writing a second write path.
    /// </remarks>
    public const int MaxReasonLength = 512;

    /// <summary>Whether this account is currently suspended.</summary>
    public static bool IsSuspended(Sw5eUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.SuspendedAt is not null;
    }

    /// <summary>
    /// Ends the session of a caller whose account has been suspended since the
    /// cookie was issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Costs one indexed read by primary key, projected to a single nullable
    /// column, on authenticated requests only. Anonymous traffic — which on
    /// this site is nearly all of it, because the reference is public — never
    /// reaches here at all, and an authenticated request already reads the
    /// account row in its handler. Doubling the cheapest query on the least
    /// busy path is the price of a suspension that means something.
    /// </para>
    /// <para>
    /// It rejects the principal <em>and</em> signs out, because rejecting alone
    /// leaves the cookie in the browser to be presented again on the next
    /// request. There is nothing to gain from making them do this once a second
    /// for eight hours.
    /// </para>
    /// </remarks>
    public static async Task RejectSuspendedAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Null when an earlier validator — the security stamp check — already
        // refused this principal. There is nothing left to reject.
        if (context.Principal is null)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var users = services.GetRequiredService<UserManager<Sw5eUser>>();

        if (!Guid.TryParse(users.GetUserId(context.Principal), out var userId))
        {
            return;
        }

        var store = services.GetRequiredService<Sw5eIdentityDbContext>();

        // Projected rather than materialised. Nothing here needs the account —
        // only the answer to one question — and loading the whole row would
        // also put it in the change tracker of a context the request's own
        // handler is about to use.
        var suspendedAt = await store.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.SuspendedAt)
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (suspendedAt is null)
        {
            return;
        }

        context.RejectPrincipal();

        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

        services.GetService<ILoggerFactory>()
            ?.CreateLogger("Sw5e.Api.Accounts")
            .LogWarning(
                "Ended a live session for suspended account {UserId}.",
                userId);
    }
}

/// <summary>
/// Adds "and is not suspended" to the framework's idea of an account that may
/// sign in.
/// </summary>
/// <remarks>
/// <para>
/// <c>IUserConfirmation</c> is the seam <c>SignInManager.CanSignInAsync</c>
/// consults when <c>SignIn.RequireConfirmedAccount</c> is set, which it is. The
/// default implementation answers <c>EmailConfirmed</c>; this answers that and
/// the suspension, so every route that can produce a session is covered by one
/// registration rather than by three handlers each remembering to ask.
/// </para>
/// <para>
/// The alternative considered and rejected was <c>LockoutEnd</c> set far in the
/// future. It would have required no new column and it would have been wrong:
/// lockout is the framework's automatic response to failed attempts, it
/// self-heals, and any stranger can cause one against any account they can
/// name. Conflating the two would mean lifting a suspension also forgave an
/// attack in progress, and an expiring lockout quietly reinstating somebody a
/// person had decided to remove.
/// </para>
/// </remarks>
public sealed class SuspensionAwareUserConfirmation : IUserConfirmation<Sw5eUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<Sw5eUser> manager, Sw5eUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Task.FromResult(user.EmailConfirmed && !AccountSuspension.IsSuspended(user));
    }
}
