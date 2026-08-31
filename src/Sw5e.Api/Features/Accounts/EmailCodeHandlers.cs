using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sw5e.Identity;
using Sw5e.Identity.Email;
using Sw5e.Identity.EmailSignIn;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Signing in with a code sent to the account's email address.
/// </summary>
/// <remarks>
/// <para>
/// This exists because passkeys, for all their advantages, assume a device the
/// person controls, a browser new enough to have the API, and an authenticator
/// they are allowed to enrol. A shared library machine, a locked-down work
/// laptop, a five-year-old tablet and a borrowed phone all fail at least one of
/// those, and this is a community reference site: excluding those readers is
/// not an acceptable price for the stronger credential.
/// </para>
/// <para>
/// What it is not is a second-class password. There is nothing here to breach,
/// because nothing durable is stored; nothing to reuse, because every code is
/// good once and for ten minutes; and nothing to phish out of a database,
/// because the database holds a slow hash of a value that has already expired
/// by the time it could be cracked. Passkeys remain the recommended path and
/// the account area offers enrolment to anybody who arrives this way.
/// </para>
/// <para>
/// <b>Three properties do the security work, and each is enforced somewhere
/// specific.</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>The request endpoint reveals nothing.</b> One status code, one body, one
/// message, and one email sent, whether or not the address has an account. See
/// <see cref="RequestAsync"/>, where both branches perform the same work in the
/// same order for the same reason the registration endpoint does.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Codes are cheap to send and expensive to guess.</b> Per-address budgets
/// and a resend cooldown live in <see cref="EmailSignInCodeService"/>; the
/// per-caller budget is the rate limiter attached to the routes. Both are
/// needed: the address limit stops one attacker mailing one victim repeatedly,
/// the caller limit stops one attacker mailing a thousand victims once each.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>A code is not a substitute for a second factor.</b> An account with an
/// authenticator app still has to produce a code from it, exactly as it would
/// after a passkey. An account with an elevated role gets a session that cannot
/// use the elevated role — see <c>StrongAuthenticationRequirement</c> — because
/// a mailbox is what every other credential on the internet is recovered
/// through, and privilege that a mailbox alone unlocks is privilege protected
/// by one factor.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal static class EmailCodeHandlers
{
    public static async Task<Results<Accepted<SignInCodeRequestedResponse>, ProblemHttpResult>> RequestAsync(
        SignInCodeRequest request,
        UserManager<Sw5eUser> users,
        EmailSignInCodeService codes,
        IAccountEmailSender email,
        IOptions<Sw5eIdentityOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        // Before the branch, never after. From here on every path produces the
        // same response.
        if (!AccountInput.TryReadEmail(request.Email, out var emailAddress, out var problem))
        {
            return problem!;
        }

        var identity = options.Value;
        var user = await users.FindByEmailAsync(emailAddress);

        // Normalised through the UserManager rather than by lower-casing here,
        // so that the code table and the account table agree about what counts
        // as the same address. Two spellings that resolve to one account but to
        // two rows here would be a per-address rate limit with a trivial
        // bypass.
        var normalized = users.NormalizeEmail(emailAddress) ?? emailAddress.ToUpperInvariant();

        // An account that cannot sign in gets a code that will not work, and
        // that is the correct behaviour rather than an oversight. Refusing to
        // issue would make the unverified and locked-out cases observably
        // faster than the ordinary one, and the redemption endpoint checks both
        // again anyway.
        var issue = await codes.IssueAsync(normalized, user?.Id, cancellationToken);

        if (issue.Issued)
        {
            // Exactly one message on each branch, so the time this endpoint
            // takes does not depend on whether the address is registered. The
            // unknown-address branch is not a courtesy that could be dropped to
            // save a send: dropping it is what would turn the response time
            // into an account-existence oracle.
            if (user is not null)
            {
                await email.SendSignInCodeAsync(
                    new AccountEmailRecipient(user.Email!, user.DisplayName),
                    issue.Code!,
                    identity.EmailSignInCodeLifetime,
                    cancellationToken);
            }
            else
            {
                await email.SendUnknownAddressSignInNoticeAsync(emailAddress, cancellationToken);
            }
        }

        // Note what this log line does not contain: the address, and the code.
        // The address because this endpoint is reachable by anyone and a log of
        // every address ever typed into it is a mailing list somebody built by
        // accident; the code because it is a live credential.
        logger.LogInformation(
            "Handled a sign-in code request (issued: {Issued}).", issue.Issued);

        // One response for every branch above: address known, address unknown,
        // address throttled. The throttled branch answers identically on
        // purpose — saying "wait" would confirm that somebody recently asked
        // for a code for this address, which is a smaller leak than account
        // existence but is still a leak, and the front end already counts the
        // cooldown down from the fixed value below.
        return TypedResults.Accepted(
            (string?)null,
            SignInCodeRequestedResponse.For(identity));
    }

    public static async Task<Results<Ok<SignInResponse>, ProblemHttpResult>> VerifyAsync(
        SignInCodeVerifyRequest request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        SignInManager<Sw5eUser> signIn,
        EmailSignInCodeService codes,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        // Every refusal below is this same value. A caller learns that it did
        // not work and nothing else — not whether the address exists, not
        // whether a code was outstanding, not whether this one had expired, and
        // not whether the digits were close.
        if (!AccountInput.TryReadEmail(request.Email, out var emailAddress, out _))
        {
            return AccountProblems.SignInFailed;
        }

        if (AccountInput.NormaliseDigits(request.Code, SignInCodeRequestedResponse.CodeLength)
            is not { } code)
        {
            return AccountProblems.SignInFailed;
        }

        var normalized = users.NormalizeEmail(emailAddress) ?? emailAddress.ToUpperInvariant();

        var redemption = await codes.RedeemAsync(normalized, code, cancellationToken);

        if (redemption.UserId is not { } userId)
        {
            return AccountProblems.SignInFailed;
        }

        var user = await users.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            // The account was deleted between the code being issued and being
            // redeemed. The code is already spent by this point, which is the
            // right outcome.
            logger.LogWarning("A redeemed sign-in code named an account that no longer exists.");
            return AccountProblems.SignInFailed;
        }

        // The same two checks the passkey path applies after a valid assertion,
        // and for the same reason: proving possession of a factor is not by
        // itself permission to enter. A locked-out account stays locked out,
        // and an account that has never confirmed its address does not get in
        // through the address.
        if (await users.IsLockedOutAsync(user))
        {
            logger.LogWarning(
                "Refused a valid sign-in code for locked-out account {UserId}.", user.Id);
            return AccountProblems.SignInFailed;
        }

        if (!await signIn.CanSignInAsync(user))
        {
            logger.LogInformation(
                "Refused a valid sign-in code for account {UserId}, which may not sign in.", user.Id);
            return AccountProblems.SignInFailed;
        }

        if (await users.GetTwoFactorEnabledAsync(user))
        {
            // Identical to what a passkey assertion does here, and deliberately
            // so. Somebody who switched on an authenticator app asked for every
            // route in to require it; a route that skipped it would be a way to
            // undo that decision by using the weaker credential.
            await AccountSessions.StorePendingTwoFactorAsync(context, users, user);

            logger.LogInformation(
                "Account {UserId} redeemed a sign-in code and is awaiting a second factor.", user.Id);

            return TypedResults.Ok(SignInResponse.MfaRequired);
        }

        await AccountSessions.SignInAsync(signIn, user, Sw5eClaims.EmailCodeMethod);
        await users.ResetAccessFailedCountAsync(user);

        logger.LogInformation("Account {UserId} signed in with an emailed code.", user.Id);

        return TypedResults.Ok(
            await AccountProfile.DescribeSignInAsync(users, user, Sw5eClaims.EmailCodeMethod));
    }
}
