using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sw5e.Identity;
using Sw5e.Identity.Email;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Registration and email verification.
/// </summary>
/// <remarks>
/// <para>
/// The organising principle of both handlers is that an unauthenticated caller
/// must not be able to learn whether an address has an account here. That
/// sounds like a small courtesy and is not: an address enumeration oracle turns
/// a breached password list from somewhere else into a targeted list for here,
/// tells a harasser whether a specific person uses the site, and hands a
/// phisher a pre-validated audience.
/// </para>
/// <para>
/// Resisting it costs real usability — nobody is ever told "that address is
/// already registered" — and the cost is paid deliberately. The information the
/// caller needs still reaches them; it goes to the mailbox, which is the one
/// place where telling the truth is safe, because only the account holder can
/// read it.
/// </para>
/// </remarks>
internal static class RegistrationHandlers
{
    /// <summary>
    /// Longest address accepted. Comfortably above the practical maximum for a
    /// deliverable address and well below anything that would make normalising
    /// or indexing it expensive.
    /// </summary>
    private const int MaxEmailLength = 254;

    private const int MaxDisplayNameLength = 64;
    private const int MinDisplayNameLength = 2;

    public static async Task<Results<Accepted<RegisterResponse>, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request,
        UserManager<Sw5eUser> users,
        IAccountEmailSender email,
        IOptions<Sw5eIdentityOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        // Input shape is validated and rejected loudly, because a malformed
        // address is not a fact about whether an account exists. The moment the
        // input is well formed, every path below converges on one response.
        if (!TryReadEmail(request.Email, out var emailAddress, out var problem) ||
            !TryReadDisplayName(request.DisplayName, out var displayName, out problem))
        {
            return problem!;
        }

        var existing = await users.FindByEmailAsync(emailAddress);

        if (existing is null)
        {
            await CreateAndInviteAsync(
                users, email, options.Value, emailAddress, displayName, logger, cancellationToken);
        }
        else
        {
            await InviteExistingAsync(
                users, email, options.Value, existing, logger, cancellationToken);
        }

        // One response, one status code, one body, for every branch above.
        return TypedResults.Accepted((string?)null, RegisterResponse.Accepted);
    }

    private static async Task CreateAndInviteAsync(
        UserManager<Sw5eUser> users,
        IAccountEmailSender email,
        Sw5eIdentityOptions options,
        string emailAddress,
        string displayName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var user = new Sw5eUser
        {
            UserName = emailAddress,
            Email = emailAddress,
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var created = await users.CreateAsync(user);

        if (!created.Succeeded)
        {
            // The overwhelmingly likely cause is the unique email index firing
            // because a concurrent request created the same account a
            // millisecond ago. That is a race, not an error the caller can act
            // on, and saying so would leak the very fact this endpoint exists
            // to hide. Logged and swallowed; the caller gets the same 202 as
            // everybody else.
            logger.LogInformation(
                "Registration did not create an account: {Errors}",
                string.Join("; ", created.Errors.Select(error => error.Code)));
            return;
        }

        // Every account starts in Community and stays there until an
        // administrator decides otherwise. Assigning it here rather than
        // relying on "no roles means community" means authorization always has
        // a positive grant to look at.
        await users.AddToRoleAsync(user, Sw5eRoles.Community);

        var token = await users.GenerateEmailConfirmationTokenAsync(user);

        await email.SendEmailVerificationAsync(
            new AccountEmailRecipient(emailAddress, displayName),
            AccountLinks.VerifyEmail(options, emailAddress, token),
            cancellationToken);

        logger.LogInformation("Registered account {UserId} and sent a verification link.", user.Id);
    }

    private static async Task InviteExistingAsync(
        UserManager<Sw5eUser> users,
        IAccountEmailSender email,
        Sw5eIdentityOptions options,
        Sw5eUser user,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Note what is not happening: the submitted display name is discarded.
        // Honouring it would let anyone who knows an address rewrite how that
        // account is presented across the site, without ever proving control of
        // anything.
        var recipient = new AccountEmailRecipient(user.Email!, user.DisplayName);
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = AccountLinks.VerifyEmail(options, user.Email!, token);

        if (user.EmailConfirmed)
        {
            // A registration attempt against a verified account is either the
            // owner who forgot they had one, or somebody probing. Both get the
            // same message, sent to the owner: a recovery link, phrased so that
            // a recipient who did not ask for it knows to ignore it. That turns
            // the probe into a notification the real owner receives.
            await email.SendPasskeyRecoveryAsync(recipient, link, cancellationToken);
            logger.LogInformation(
                "Registration attempt against verified account {UserId}; sent a recovery link.",
                user.Id);
        }
        else
        {
            // An unverified account is a registration nobody finished. Resend
            // rather than refuse; the previous token is invalidated by the new
            // one sharing the account's security stamp.
            await email.SendEmailVerificationAsync(recipient, link, cancellationToken);
            logger.LogInformation(
                "Resent the verification link for unverified account {UserId}.", user.Id);
        }
    }

    public static async Task<Results<Ok<VerifyEmailResponse>, ProblemHttpResult>> VerifyEmailAsync(
        VerifyEmailRequest request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        AccountStateCookies state,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        if (!TryReadEmail(request.Email, out var emailAddress, out var problem))
        {
            return problem!;
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return AccountProblems.VerificationFailed;
        }

        var user = await users.FindByEmailAsync(emailAddress);

        // An unknown address and a bad token produce the same refusal. Anything
        // else — a 404 here, a 400 there — is an enumeration oracle wearing a
        // status code.
        if (user is null)
        {
            return AccountProblems.VerificationFailed;
        }

        var result = await users.ConfirmEmailAsync(user, request.Token);

        if (!result.Succeeded)
        {
            logger.LogInformation("Rejected an email verification token for account {UserId}.", user.Id);
            return AccountProblems.VerificationFailed;
        }

        // Rotating the security stamp immediately after a successful
        // verification is what makes this flow safe to reuse for recovery.
        // Every token generated for this account before now — every recovery
        // link anybody has ever requested for it, whether they were entitled to
        // or not — is derived from the old stamp and stops working here. Any
        // session already established is re-evaluated within the security stamp
        // validation interval and dropped, which is the correct outcome when
        // somebody has just proved mailbox control in order to re-credential.
        await users.UpdateSecurityStampAsync(user);

        var stamp = await users.GetSecurityStampAsync(user);
        state.StoreEnrollmentTicket(context, user.Id, stamp);

        logger.LogInformation(
            "Verified the address on account {UserId} and opened a passkey enrolment window.",
            user.Id);

        return TypedResults.Ok(new VerifyEmailResponse(
            "verified",
            DateTimeOffset.UtcNow.Add(AccountStateCookies.EnrollmentLifetime)));
    }

    private static bool TryReadEmail(
        string? value,
        out string emailAddress,
        out ProblemHttpResult? problem)
    {
        emailAddress = value?.Trim() ?? string.Empty;
        problem = null;

        if (emailAddress.Length is 0 or > MaxEmailLength)
        {
            problem = AccountProblems.Invalid(
                $"An email address of 1 to {MaxEmailLength} characters is required.");
            return false;
        }

        // Deliberately shallow. The only address validation that means anything
        // is whether a message sent to it arrives, and that check happens
        // anyway two lines later. A stricter regular expression here would
        // reject deliverable addresses and stop nothing.
        var at = emailAddress.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 ||
            at == emailAddress.Length - 1 ||
            emailAddress.IndexOf('@', at + 1) >= 0 ||
            emailAddress.Any(char.IsWhiteSpace) ||
            emailAddress.Any(char.IsControl))
        {
            problem = AccountProblems.Invalid("That is not a valid email address.");
            return false;
        }

        return true;
    }

    private static bool TryReadDisplayName(
        string? value,
        out string displayName,
        out ProblemHttpResult? problem)
    {
        displayName = value?.Trim() ?? string.Empty;
        problem = null;

        if (displayName.Length is < MinDisplayNameLength or > MaxDisplayNameLength)
        {
            problem = AccountProblems.Invalid(
                $"A display name of {MinDisplayNameLength} to {MaxDisplayNameLength} characters is required.");
            return false;
        }

        // Control characters are stripped at the door rather than at every
        // point of display. A newline or a bidirectional override in a name is
        // never legitimate and is a standard way to make one account's name
        // impersonate another's in a list.
        foreach (var character in displayName)
        {
            if (char.IsControl(character) ||
                char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format)
            {
                problem = AccountProblems.Invalid(
                    "A display name cannot contain control or formatting characters.");
                return false;
            }
        }

        return true;
    }
}
