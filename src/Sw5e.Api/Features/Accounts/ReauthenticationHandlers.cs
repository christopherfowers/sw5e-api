using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;
using Sw5e.Identity.TwoFactor;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Proving a second factor on a session that already exists.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the rule in <see cref="Sw5eClaims.AuthenticationMethod"/>
/// — that a session records how it was established, once, and never gains
/// strength from the account changing underneath it — is correct and was also,
/// on its own, a dead end. Somebody who signed in with an emailed code and then
/// enrolled a passkey held every credential the elevated area asks for and
/// still could not open it. The only remedy on offer was to sign out and sign
/// in again, which is a strange thing to ask of somebody who has just proved
/// possession of the device sitting in front of them.
/// </para>
/// <para>
/// So: prove it now instead. The account performs a real assertion, or produces
/// a real authenticator code, against the account it is already signed in as,
/// and the session cookie is re-issued carrying the method that was actually
/// demonstrated.
/// </para>
/// <para>
/// Nothing is weakened by this, and it is worth being precise about why. The
/// property being defended is that a compromised mailbox is not an
/// administrative takeover. An attacker holding the mailbox can reach these two
/// endpoints — they hold a session — and cannot pass either of them, because
/// passing requires the passkey or the authenticator secret, which is exactly
/// what the mailbox does not give them. The claim still means "demonstrated
/// during this session". It stops meaning "demonstrated at the instant the
/// session opened", and that was never the part doing the work.
/// </para>
/// <para>
/// Note also what these do <em>not</em> do: they never create a session. Both
/// routes require one, so neither is a way in. They only ever add to what the
/// caller already had.
/// </para>
/// </remarks>
internal static class ReauthenticationHandlers
{
    public static async Task<IResult> BeginPasskeyAsync(
        HttpContext context,
        UserManager<Sw5eUser> users,
        IPasskeyHandler<Sw5eUser> passkeys,
        AccountStateCookies state)
    {
        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return AccountProblems.NotAuthenticated;
        }

        // Named, unlike the sign-in ceremony, which passes null so that the
        // browser offers whatever it holds and the server learns nothing. Here
        // the server already knows who is asking, so there is no secret left to
        // keep, and naming the account produces an allowCredentials list — the
        // browser then offers this account's keys rather than every key it
        // holds for the site, and somebody with two accounts is not invited to
        // pick the wrong one and be told it failed.
        var options = await passkeys.MakeRequestOptionsAsync(user, context);

        state.StoreLoginChallenge(context, options.AssertionState!);

        return Results.Content(options.RequestOptionsJson, "application/json");
    }

    public static async Task<Results<Ok<CurrentUserResponse>, ProblemHttpResult>> CompletePasskeyAsync(
        PasskeyCredentialRequest request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        SignInManager<Sw5eUser> signIn,
        IPasskeyHandler<Sw5eUser> passkeys,
        AccountStateCookies state,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LogCategories.Accounts);

        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return AccountProblems.NotAuthenticated;
        }

        var challenge = state.ReadLoginChallenge(context);

        // Spent whether or not the assertion succeeds, for the same reason the
        // sign-in ceremony spends its own: a challenge that survives a failure
        // is a challenge that can be answered repeatedly.
        AccountStateCookies.Clear(context, AccountStateCookies.LoginChallengeCookie);

        if (challenge is null || request.Credential is not { } credential)
        {
            return AccountProblems.ReauthenticationFailed;
        }

        var assertion = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
        {
            CredentialJson = credential.GetRawText(),
            AssertionState = challenge,
            HttpContext = context,
        });

        if (!assertion.Succeeded)
        {
            logger.LogInformation(
                "A re-authentication assertion failed for account {UserId}: {Reason}",
                user.Id,
                assertion.Failure.Message);

            return AccountProblems.ReauthenticationFailed;
        }

        // The whole point of the endpoint. A verified assertion proves somebody
        // holds a passkey for this site; it does not prove they hold one for
        // the account whose session is being raised. Without this comparison,
        // anybody with an account of their own could raise a session they had
        // taken over by asserting their own credential against it.
        if (assertion.User.Id != user.Id)
        {
            logger.LogWarning(
                "Refused to raise the session for account {UserId} with a passkey belonging to {AssertedUserId}.",
                user.Id,
                assertion.User.Id);

            return AccountProblems.ReauthenticationFailed;
        }

        if (await users.IsLockedOutAsync(user) || !await signIn.CanSignInAsync(user))
        {
            logger.LogWarning(
                "Refused to raise the session for account {UserId}, which may not sign in.", user.Id);

            return AccountProblems.ReauthenticationFailed;
        }

        // Writes back the signature counter and the backup-state flags. The
        // counter is the framework's clone detection and it is worthless unless
        // every assertion updates it — including the ones that happen here
        // rather than on the sign-in path.
        var updated = await users.AddOrUpdatePasskeyAsync(user, assertion.Passkey);

        if (!updated.Succeeded)
        {
            logger.LogError("Could not update the passkey record for account {UserId}.", user.Id);
            return AccountProblems.ReauthenticationFailed;
        }

        await AccountSessions.SignInAsync(signIn, user, Sw5eClaims.PasskeyMethod);
        await users.ResetAccessFailedCountAsync(user);

        logger.LogInformation("Account {UserId} re-authenticated with a passkey.", user.Id);

        // The profile itself rather than the sign-in envelope. A sign-in may
        // answer "mfaRequired" with no account attached; this cannot, because
        // the caller was already signed in when they arrived. Returning a shape
        // with a status that is always the same and a user that is never null
        // would hand the client a branch it can never take.
        return TypedResults.Ok(
            await AccountProfile.DescribeAsync(users, user, Sw5eClaims.PasskeyMethod));
    }

    public static async Task<Results<Ok<CurrentUserResponse>, ProblemHttpResult>> CompleteTotpAsync(
        TotpVerifyRequest request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        SignInManager<Sw5eUser> signIn,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LogCategories.Accounts);

        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return AccountProblems.NotAuthenticated;
        }

        var code = AccountInput.NormaliseDigits(request.Code, Rfc6238TimeBasedOneTimePassword.Digits);

        if (code is null)
        {
            return AccountProblems.Invalid("A six-digit code from the authenticator app is required.");
        }

        // Enrolment is a different endpoint with different consequences — it
        // switches two-factor authentication on and mints recovery codes.
        // Refusing here when there is nothing enrolled keeps the two apart, so
        // a client that posts to the wrong one meets a refusal rather than a
        // surprise.
        if (!await users.GetTwoFactorEnabledAsync(user))
        {
            return AccountProblems.Invalid(
                "There is no authenticator app on this account yet. Set one up first.");
        }

        // Checked before the code is examined. Without it, this endpoint is an
        // unmetered six-digit guessing oracle for anybody holding a session.
        if (await users.IsLockedOutAsync(user))
        {
            logger.LogWarning(
                "Refused to raise the session for locked-out account {UserId}.", user.Id);

            return AccountProblems.SignInFailed;
        }

        var valid = await users.VerifyTwoFactorTokenAsync(
            user,
            users.Options.Tokens.AuthenticatorTokenProvider,
            code);

        if (!valid)
        {
            // Counted against the lockout exactly as a failed sign-in is, which
            // is what turns the check above into a real bound rather than a
            // condition that never fires.
            await users.AccessFailedAsync(user);

            logger.LogInformation(
                "Rejected a re-authentication code for account {UserId}.", user.Id);

            return AccountProblems.Invalid("That code was not accepted. Check the app and try again.");
        }

        await users.ResetAccessFailedCountAsync(user);
        await AccountSessions.SignInAsync(signIn, user, Sw5eClaims.AuthenticatorMethod);

        logger.LogInformation("Account {UserId} re-authenticated with an authenticator code.", user.Id);

        return TypedResults.Ok(
            await AccountProfile.DescribeAsync(users, user, Sw5eClaims.AuthenticatorMethod));
    }
}
