using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;
using Sw5e.Identity.Email;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Passkey enrolment and passkey sign-in.
/// </summary>
/// <remarks>
/// <para>
/// None of the cryptography lives here. Challenge generation, client data
/// parsing, attestation object decoding, signature verification, relying-party
/// and origin binding, user-verification enforcement and signature counter
/// handling are all <see cref="IPasskeyHandler{TUser}"/>'s, which is the
/// framework's own WebAuthn implementation. What this file owns is everything
/// around it: who is allowed to start a ceremony, where the challenge is kept
/// between the two requests, what a failure is allowed to say, and — the part
/// that needed the most care — what happens after a signature verifies.
/// </para>
/// <para>
/// On that last point, note the deliberate avoidance of
/// <c>SignInManager.PasskeySignInAsync</c>. It does almost exactly the right
/// thing, and then calls the internal sign-in with <c>bypassTwoFactor: true</c>
/// — a defensible framework default, on the reasoning that a user-verifying
/// passkey is already two factors. This platform offers TOTP as a second factor
/// on top of that, and a user who switches it on has asked for it. Routing
/// sign-in through that method would have left every one of those accounts
/// entered without the factor they enabled, and the TOTP feature would have
/// been decorative. So the assertion is performed directly and the two-factor
/// decision is made here, in the open.
/// </para>
/// </remarks>
internal static class PasskeyHandlers
{
    /// <summary>Longest label accepted for a passkey.</summary>
    private const int MaxPasskeyNameLength = 64;

    public static async Task<IResult> BeginRegistrationAsync(
        HttpContext context,
        UserManager<Sw5eUser> users,
        IPasskeyHandler<Sw5eUser> passkeys,
        AccountStateCookies state)
    {
        var user = await ResolveEnrollingUserAsync(context, users, state);

        if (user is null)
        {
            return AccountProblems.NotAuthenticated;
        }

        var options = await passkeys.MakeCreationOptionsAsync(
            new PasskeyUserEntity
            {
                // The account's own identifier. This is the value the
                // authenticator stores and returns on a later assertion, so it
                // must be stable for the life of the account — which is exactly
                // why it is the primary key and not the email address, which
                // can change.
                Id = user.Id.ToString(),
                Name = user.Email!,
                DisplayName = user.DisplayName,
            },
            context);

        state.StoreRegistrationChallenge(context, options.AttestationState!);

        // Returned verbatim. The framework produced JSON that
        // PublicKeyCredential.parseCreationOptionsFromJSON() understands
        // exactly; re-serialising it through a response type of ours would be
        // an opportunity to corrupt a challenge and gain nothing.
        return Results.Content(options.CreationOptionsJson, "application/json");
    }

    public static async Task<Results<Created<PasskeyRegisteredResponse>, ProblemHttpResult>>
        CompleteRegistrationAsync(
            PasskeyCredentialRequest request,
            HttpContext context,
            UserManager<Sw5eUser> users,
            IPasskeyHandler<Sw5eUser> passkeys,
            AccountStateCookies state,
            IAccountEmailSender email,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        var user = await ResolveEnrollingUserAsync(context, users, state);

        if (user is null)
        {
            return AccountProblems.NotAuthenticated;
        }

        var challenge = state.ReadRegistrationChallenge(context);

        // Spent whether or not what follows succeeds. A challenge that survives
        // a failed attempt is a challenge an attacker may keep answering, which
        // is the difference between one guess and unlimited ones.
        AccountStateCookies.Clear(context, AccountStateCookies.RegistrationChallengeCookie);

        if (challenge is null || request.Credential is not { } credential)
        {
            return AccountProblems.PasskeyFailed;
        }

        var attestation = await passkeys.PerformAttestationAsync(new PasskeyAttestationContext
        {
            CredentialJson = credential.GetRawText(),
            AttestationState = challenge,
            HttpContext = context,
        });

        if (!attestation.Succeeded)
        {
            logger.LogInformation(
                "Passkey attestation failed for account {UserId}: {Reason}",
                user.Id,
                attestation.Failure.Message);

            return AccountProblems.PasskeyFailed;
        }

        // The handler already checks that the attested user entity matches the
        // one the challenge was minted for. Checking again is cheap, and the
        // thing being defended — a credential being attached to an account
        // other than the one that asked for it — is severe enough that one
        // redundant comparison is a good trade.
        if (!string.Equals(attestation.UserEntity.Id, user.Id.ToString(), StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Refused a passkey attested for a different account than the one enrolling ({UserId}).",
                user.Id);

            return AccountProblems.PasskeyFailed;
        }

        var passkey = attestation.Passkey;
        passkey.Name = ReadName(request.Name);

        var stored = await users.AddOrUpdatePasskeyAsync(user, passkey);

        if (!stored.Succeeded)
        {
            logger.LogError(
                "Could not store a verified passkey for account {UserId}: {Errors}",
                user.Id,
                string.Join("; ", stored.Errors.Select(error => error.Code)));

            return AccountProblems.PasskeyFailed;
        }

        // The enrolment window closes the moment it has been used for what it
        // was opened for. Leaving it open would let a single emailed link enrol
        // any number of credentials over the next ten minutes.
        AccountStateCookies.Clear(context, AccountStateCookies.EnrollmentCookie);

        // After the fact, and to the address on file rather than to whoever is
        // holding the browser. A new credential on an account is the single
        // clearest signal of a takeover in progress, and this is the message
        // that lets the real owner notice one.
        await email.SendSecurityNoticeAsync(
            new AccountEmailRecipient(user.Email!, user.DisplayName),
            "A new passkey was added to your account.",
            cancellationToken);

        logger.LogInformation("Enrolled a passkey for account {UserId}.", user.Id);

        // Base64url, matching how WebAuthn identifies a credential everywhere
        // else, so the value round-trips through the browser API unchanged.
        var credentialId = Base64Url.EncodeToString(passkey.CredentialId);

        // 201 with no Location header: the credential is not separately
        // retrievable, and inventing a URL for it would promise a resource that
        // does not exist.
        return TypedResults.Created(
            (string?)null,
            new PasskeyRegisteredResponse(credentialId, passkey.Name, passkey.CreatedAt));
    }

    /// <summary>Removes one of the signed-in account's passkeys.</summary>
    /// <remarks>
    /// <para>
    /// The counterpart to enrolment, and the reason the account area is worth
    /// having: a reader who loses a device needs to be able to cut it off
    /// without asking anybody. It requires a full session — an enrolment ticket
    /// is permission to add a credential after proving mailbox control, and
    /// letting it also remove one would turn an intercepted recovery link into
    /// a way to strip an account of the credentials it already had.
    /// </para>
    /// <para>
    /// The last credential is never removed; see
    /// <see cref="AccountProblems.LastCredential"/>. Note that this is checked
    /// against the account's own list rather than against a count held
    /// anywhere, so two concurrent removals cannot both believe they are not
    /// the last one and empty the account between them — the second one reads a
    /// list of one and refuses.
    /// </para>
    /// </remarks>
    public static async Task<Results<Ok<PasskeyRemovedResponse>, ProblemHttpResult>> RemoveAsync(
        string credentialId,
        HttpContext context,
        UserManager<Sw5eUser> users,
        IAccountEmailSender email,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return AccountProblems.NotAuthenticated;
        }

        // The identifier arrives base64url, the way WebAuthn spells it. Anything
        // that is not is not a credential this account holds, and saying so is
        // the same answer as not finding it.
        if (!Base64Url.IsValid(credentialId) ||
            Base64Url.DecodeFromChars(credentialId) is not { Length: > 0 } wanted)
        {
            return AccountProblems.NoSuchCredential;
        }

        var passkeys = await users.GetPasskeysAsync(user);

        var passkey = passkeys.FirstOrDefault(
            candidate => CryptographicOperations.FixedTimeEquals(candidate.CredentialId, wanted));

        if (passkey is null)
        {
            return AccountProblems.NoSuchCredential;
        }

        if (passkeys.Count == 1)
        {
            logger.LogInformation(
                "Refused to remove the only passkey on account {UserId}.", user.Id);

            return AccountProblems.LastCredential;
        }

        var removed = await users.RemovePasskeyAsync(user, passkey.CredentialId);

        if (!removed.Succeeded)
        {
            logger.LogError(
                "Could not remove a passkey from account {UserId}: {Errors}",
                user.Id,
                string.Join("; ", removed.Errors.Select(error => error.Code)));

            return AccountProblems.NoSuchCredential;
        }

        // To the address on file, not to whoever is holding the browser. Losing
        // a credential is as strong a takeover signal as gaining one, and this
        // is the message that lets the real owner notice somebody else pruning
        // their account.
        await email.SendSecurityNoticeAsync(
            new AccountEmailRecipient(user.Email!, user.DisplayName),
            "A passkey was removed from your account.",
            cancellationToken);

        logger.LogInformation("Removed a passkey from account {UserId}.", user.Id);

        return TypedResults.Ok(PasskeyRemovedResponse.Removed);
    }

    public static async Task<IResult> BeginLoginAsync(
        HttpContext context,
        IPasskeyHandler<Sw5eUser> passkeys,
        AccountStateCookies state)
    {
        // No user, and no way to name one. Passing null asks the framework for
        // request options with an empty allowCredentials list, so the browser
        // offers whichever discoverable passkeys it holds for this site and the
        // server never has to be told who is signing in.
        //
        // That is what makes this endpoint enumeration-proof rather than merely
        // discreet: there is no input to vary, so there is no response to
        // compare. It is also why IdentityPasskeyOptions.ResidentKeyRequirement
        // is set to "required" — a non-discoverable credential would have to be
        // named here, and naming it would mean asking for an email address
        // first.
        var options = await passkeys.MakeRequestOptionsAsync(user: null, context);

        state.StoreLoginChallenge(context, options.AssertionState!);

        return Results.Content(options.RequestOptionsJson, "application/json");
    }

    public static async Task<Results<Ok<SignInResponse>, ProblemHttpResult>> CompleteLoginAsync(
        PasskeyCredentialRequest request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        SignInManager<Sw5eUser> signIn,
        IPasskeyHandler<Sw5eUser> passkeys,
        AccountStateCookies state,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        var challenge = state.ReadLoginChallenge(context);
        AccountStateCookies.Clear(context, AccountStateCookies.LoginChallengeCookie);

        if (challenge is null || request.Credential is not { } credential)
        {
            return AccountProblems.SignInFailed;
        }

        var assertion = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
        {
            CredentialJson = credential.GetRawText(),
            AssertionState = challenge,
            HttpContext = context,
        });

        if (!assertion.Succeeded)
        {
            logger.LogInformation("Passkey assertion failed: {Reason}", assertion.Failure.Message);
            return AccountProblems.SignInFailed;
        }

        var user = assertion.User;

        // A verified signature is not by itself permission to enter. The
        // account may be locked out, and it may never have confirmed its
        // address — SignInManager.PasskeySignInAsync would have applied both of
        // these through PreSignInCheck, and performing the assertion directly
        // means applying them here instead of inheriting them.
        if (await users.IsLockedOutAsync(user))
        {
            logger.LogWarning("Refused a valid passkey assertion for locked-out account {UserId}.", user.Id);
            return AccountProblems.SignInFailed;
        }

        if (!await signIn.CanSignInAsync(user))
        {
            logger.LogInformation("Refused a valid passkey assertion for account {UserId}, which may not sign in.", user.Id);
            return AccountProblems.SignInFailed;
        }

        // Persists the updated signature counter and the backup-state flags the
        // authenticator reported. The counter is the framework's clone
        // detection: an authenticator that presents a counter lower than the
        // one on record has been duplicated, and the check is worthless unless
        // the new value is written back after every assertion.
        var updated = await users.AddOrUpdatePasskeyAsync(user, assertion.Passkey);

        if (!updated.Succeeded)
        {
            logger.LogError("Could not update the passkey record for account {UserId}.", user.Id);
            return AccountProblems.SignInFailed;
        }

        if (await users.GetTwoFactorEnabledAsync(user))
        {
            await StorePendingTwoFactorAsync(context, users, user);
            logger.LogInformation("Account {UserId} passed a passkey assertion and is awaiting a second factor.", user.Id);
            return TypedResults.Ok(SignInResponse.MfaRequired);
        }

        // isPersistent is false, always. A session that outlives the browser is
        // a session that outlives the person walking away from the machine, and
        // this platform has nothing so tedious to sign into that it is worth
        // it. The sliding eight-hour window covers a working day.
        await signIn.SignInAsync(user, isPersistent: false);

        // A successful sign-in clears the counter, so a locked-out account that
        // recovers does not stay one failure away from being locked again.
        await users.ResetAccessFailedCountAsync(user);

        logger.LogInformation("Account {UserId} signed in with a passkey.", user.Id);

        return TypedResults.Ok(SignInResponse.Authenticated(await AccountProfile.DescribeAsync(users, user)));
    }

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
    private static async Task StorePendingTwoFactorAsync(
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

    /// <summary>
    /// Works out which account is enrolling a passkey: the signed-in one, or
    /// the one named by an unexpired enrolment ticket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is checked first, so a stale enrolment cookie can never
    /// redirect a signed-in caller's enrolment onto a different account.
    /// </para>
    /// <para>
    /// A ticket is only honoured when its security stamp still matches the
    /// account's. That comparison is what bounds the recovery flow: issuing a
    /// new recovery link rotates the stamp, so at most one outstanding ticket
    /// per account is ever live, and a ticket minted from a link that was
    /// intercepted last week is dead the moment the owner requests their own.
    /// </para>
    /// </remarks>
    private static async Task<Sw5eUser?> ResolveEnrollingUserAsync(
        HttpContext context,
        UserManager<Sw5eUser> users,
        AccountStateCookies state)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return await users.GetUserAsync(context.User);
        }

        if (state.ReadEnrollmentTicket(context) is not { } ticket)
        {
            return null;
        }

        var user = await users.FindByIdAsync(ticket.UserId.ToString());

        if (user is null)
        {
            return null;
        }

        var stamp = await users.GetSecurityStampAsync(user);

        // Ordinal, fixed-length comparison of a value that is not secret to the
        // account holder and is never guessed a character at a time; the
        // interesting property is that it matches exactly, not that it matches
        // in constant time.
        return string.Equals(stamp, ticket.SecurityStamp, StringComparison.Ordinal) ? user : null;
    }

    private static string? ReadName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // Truncated rather than rejected. The label is cosmetic, it is chosen
        // by the account holder for their own benefit, and failing an otherwise
        // valid enrolment over it would be a poor trade.
        return trimmed.Length > MaxPasskeyNameLength
            ? trimmed[..MaxPasskeyNameLength]
            : trimmed;
    }
}
