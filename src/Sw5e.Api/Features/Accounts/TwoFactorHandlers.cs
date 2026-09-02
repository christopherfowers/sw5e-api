using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;
using Sw5e.Identity.Email;
using Sw5e.Identity.TwoFactor;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Authenticator-app (TOTP) enrolment, and the second half of a sign-in that
/// needs one.
/// </summary>
/// <remarks>
/// <para>
/// A passkey with user verification is already two factors, so TOTP here is a
/// third — for accounts that hold privileges worth the extra step, and for
/// people who simply want it. Because it is optional, the one thing that must
/// never happen is a route into an account that skips it once it has been
/// turned on; see PasskeyHandlers for why the framework's own passkey sign-in
/// is not used.
/// </para>
/// <para>
/// Verification is a single endpoint serving two callers, and that is a
/// deliberate choice rather than an accident of routing. "Prove you hold the
/// authenticator" is one operation; whether it finishes an enrolment or
/// finishes a sign-in depends on which half-finished flow the caller is
/// actually in, which is decided by cookie state that only the server can
/// write. Nothing in the request body selects between them, so a caller cannot
/// steer themselves into the wrong branch.
/// </para>
/// </remarks>
internal static class TwoFactorHandlers
{
    /// <summary>
    /// How many single-use recovery codes are issued when two-factor
    /// authentication is switched on.
    /// </summary>
    /// <remarks>
    /// Enough that losing a few to a bad transcription does not matter, few
    /// enough that they stay a list somebody will actually store carefully.
    /// </remarks>
    private const int RecoveryCodeCount = 10;

    public static async Task<Results<Ok<TotpEnrollmentResponse>, ProblemHttpResult>> EnrollAsync(
        HttpContext context,
        UserManager<Sw5eUser> users)
    {
        // The route already requires an authenticated principal; this catches
        // the case where the cookie is valid but the account behind it is gone.
        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return AccountProblems.NotAuthenticated;
        }

        // A fresh secret on every call, even if one already exists. Re-issuing
        // rather than re-displaying means a secret that was shown once and then
        // exposed — a screenshot, a shoulder, an abandoned browser tab — cannot
        // be recovered by asking again, and it makes an interrupted enrolment
        // safe to restart.
        await users.ResetAuthenticatorKeyAsync(user);

        var key = await users.GetAuthenticatorKeyAsync(user)
            ?? throw new InvalidOperationException(
                "The authenticator key was not generated. The authenticator token provider is missing.");

        // Note what has not happened: two-factor authentication is not enabled
        // yet. It is switched on only by VerifyAsync, once a code from this
        // secret has actually been produced. Enabling it here would let a
        // mis-scanned QR code lock the account holder out of their own account
        // with no way back.
        // Both forms of the same secret, because scanning is not universally
        // available: a desktop authenticator has no camera, a screen reader user
        // has no picture to point one at, and somebody enrolling on the phone
        // that is displaying the QR code cannot photograph it with itself. The
        // manual string and the URI encode exactly the same bytes.
        return TypedResults.Ok(new TotpEnrollmentResponse(
            AuthenticatorUri.ForManualEntry(key),
            AuthenticatorUri.Build(user.Email!, key)));
    }

    public static async Task<IResult> VerifyAsync(
        TotpVerifyRequest request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        SignInManager<Sw5eUser> signIn,
        IAccountEmailSender email,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategories.Accounts);
        var code = AccountInput.NormaliseDigits(request.Code, Rfc6238TimeBasedOneTimePassword.Digits);

        if (code is null)
        {
            return AccountProblems.Invalid("A six-digit code from the authenticator app is required.");
        }

        // Sign-in first. A caller holding the pending two-factor cookie is
        // mid-sign-in and cannot also be signed in, so the branches cannot
        // overlap — but checking in this order means that if they ever somehow
        // did, the caller is treated as the less privileged of the two.
        //
        // The account is read out of the pending state here, before the sign-in
        // consumes it, so that the success path below has it in hand.
        if (await signIn.GetTwoFactorAuthenticationUserAsync() is { } pending)
        {
            return await CompleteSignInAsync(users, signIn, pending, code, logger);
        }

        if (await users.GetUserAsync(context.User) is { } user)
        {
            return await CompleteEnrollmentAsync(users, email, user, code, logger, cancellationToken);
        }

        return AccountProblems.NotAuthenticated;
    }

    private static async Task<IResult> CompleteSignInAsync(
        UserManager<Sw5eUser> users,
        SignInManager<Sw5eUser> signIn,
        Sw5eUser user,
        string code,
        ILogger logger)
    {
        // The framework's method does the work that matters here: it re-checks
        // the lockout before verifying, records a failure against the account's
        // access-failed counter when the code is wrong — which is what makes
        // repeated guessing actually lock the account rather than merely fail —
        // and issues the session cookie on success.
        var result = await signIn.TwoFactorAuthenticatorSignInAsync(
            code,
            isPersistent: false,

            // Never remember the machine. A remembered client is a cookie that
            // silently skips the second factor on future sign-ins, which is a
            // standing waiver of the protection the account holder deliberately
            // switched on.
            rememberClient: false);

        if (!result.Succeeded)
        {
            // Locked out, wrong code and expired pending state are one answer.
            // Distinguishing the lockout would tell an attacker their guessing
            // is having an effect and would confirm the account exists.
            logger.LogInformation("A two-factor sign-in attempt failed ({Result}).", result);
            return AccountProblems.SignInFailed;
        }

        await users.ResetAccessFailedCountAsync(user);

        // Re-issued immediately, carrying the claim that says an authenticator
        // code was produced during this sign-in. The framework's two-factor
        // method writes the session cookie itself and offers no way to add a
        // claim to it, so the choice is between reimplementing its lockout
        // handling — which is the part of it worth keeping — and writing the
        // cookie a second time. The second cookie replaces the first in the
        // same response; the cost is a few hundred bytes on one response, and
        // what it buys is that no sign-in route can produce an unstamped
        // session.
        await AccountSessions.SignInAsync(signIn, user, Sw5eClaims.AuthenticatorMethod);

        logger.LogInformation("Account {UserId} completed a two-factor sign-in.", user.Id);

        return TypedResults.Ok(
            await AccountProfile.DescribeSignInAsync(users, user, Sw5eClaims.AuthenticatorMethod));
    }

    private static async Task<IResult> CompleteEnrollmentAsync(
        UserManager<Sw5eUser> users,
        IAccountEmailSender email,
        Sw5eUser user,
        string code,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var valid = await users.VerifyTwoFactorTokenAsync(
            user,
            users.Options.Tokens.AuthenticatorTokenProvider,
            code);

        if (!valid)
        {
            // Counted against the lockout exactly as a failed sign-in is.
            // Enrolment verification takes the same six-digit guess as sign-in
            // verification does, so leaving it uncounted would offer an
            // unlimited oracle to anyone holding a stolen session.
            await users.AccessFailedAsync(user);

            logger.LogInformation("Rejected a two-factor enrolment code for account {UserId}.", user.Id);
            return AccountProblems.Invalid("That code was not accepted. Check the app and try again.");
        }

        await users.ResetAccessFailedCountAsync(user);

        var enabled = await users.SetTwoFactorEnabledAsync(user, true);

        if (!enabled.Succeeded)
        {
            logger.LogError("Could not enable two-factor authentication for account {UserId}.", user.Id);
            return AccountProblems.Invalid("Two-factor authentication could not be enabled.");
        }

        // Generated after enabling, and returned exactly once. The manager
        // stores them hashed, so this response is the only time they exist in
        // readable form anywhere.
        var recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

        await email.SendSecurityNoticeAsync(
            new AccountEmailRecipient(user.Email!, user.DisplayName),
            "Two-factor authentication was switched on for your account.",
            cancellationToken);

        logger.LogInformation("Enabled two-factor authentication for account {UserId}.", user.Id);

        return TypedResults.Ok(new TotpEnabledResponse("enabled", [.. recoveryCodes ?? []]));
    }

}
