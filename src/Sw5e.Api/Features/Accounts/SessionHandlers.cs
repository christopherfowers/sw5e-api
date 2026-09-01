using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Accounts;

/// <summary>Reading and ending the current session.</summary>
internal static class SessionHandlers
{
    public static async Task<Results<Ok<CurrentUserResponse>, ProblemHttpResult>> GetCurrentUserAsync(
        HttpContext context,
        UserManager<Sw5eUser> users)
    {
        // The route requires an authenticated principal, so reaching this
        // handler at all means a valid session cookie. The account can still be
        // missing: a cookie outlives a deleted account until the security stamp
        // validator next runs, and the correct answer in that window is that
        // there is nobody here, not a response describing a ghost.
        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return AccountProblems.NotAuthenticated;
        }

        return TypedResults.Ok(await AccountProfile.DescribeAsync(users, user, context.User));
    }

    public static async Task<NoContent> LogoutAsync(
        HttpContext context,
        SignInManager<Sw5eUser> signIn)
    {
        // Clears the session cookie.
        await signIn.SignOutAsync();

        // And every partial credential alongside it. A caller who signs out
        // midway through a two-factor challenge, or with an enrolment window
        // open, must not leave either behind on a shared machine — signing out
        // has to mean the browser holds nothing that gets anybody anywhere.
        await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        await context.SignOutAsync(IdentityConstants.TwoFactorRememberMeScheme);

        AccountStateCookies.Clear(context, AccountStateCookies.EnrollmentCookie);
        AccountStateCookies.Clear(context, AccountStateCookies.RegistrationChallengeCookie);
        AccountStateCookies.Clear(context, AccountStateCookies.LoginChallengeCookie);

        // 204 whether or not there was a session. Sign-out is idempotent by
        // nature, and answering 401 to somebody whose session already expired
        // would refuse to clear the cookies precisely when clearing them
        // matters.
        return TypedResults.NoContent();
    }
}
