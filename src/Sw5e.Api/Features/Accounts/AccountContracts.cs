using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Everything the account endpoints accept and return.
/// </summary>
/// <remarks>
/// Collected in one file because the security properties of this API are as
/// much about what the responses <em>omit</em> as about what they contain, and
/// that is only reviewable when they can be read side by side.
/// </remarks>
/// <summary>Opens a registration, or asks for a recovery link.</summary>
/// <param name="Email">The address to send the link to.</param>
/// <param name="DisplayName">
/// The name to show beside contributions. Ignored when the address already has
/// an account — otherwise anyone could rename a stranger's account by
/// "registering" it again.
/// </param>
internal sealed record RegisterRequest(string? Email, string? DisplayName);

/// <summary>
/// The only answer <c>register</c> ever gives.
/// </summary>
/// <remarks>
/// One shape, one status code, one wording, whether the address was free, was
/// already taken by a verified account, or was already taken by an unverified
/// one. A caller learns that the request was accepted and nothing else. See
/// RegistrationEndpoints for why that matters more than the better error
/// message it costs.
/// </remarks>
internal sealed record RegisterResponse(string Status, string Message)
{
    public static RegisterResponse Accepted { get; } = new(
        "pending",
        "If that address can be registered, a message with the next step is on its way. " +
        "Check the inbox, including the spam folder.");
}

/// <summary>Completes email verification with the token from the emailed link.</summary>
internal sealed record VerifyEmailRequest(string? Email, string? Token);

/// <summary>
/// The result of a successful verification.
/// </summary>
/// <param name="Status">Always <c>verified</c>.</param>
/// <param name="EnrollmentExpiresAt">
/// When the passkey enrolment window that this verification opened closes. The
/// front end shows a countdown from it; the server enforces it independently
/// and does not trust this value coming back.
/// </param>
internal sealed record VerifyEmailResponse(string Status, DateTimeOffset EnrollmentExpiresAt);

/// <summary>
/// Hands back a credential produced by <c>navigator.credentials.create()</c> or
/// <c>navigator.credentials.get()</c>.
/// </summary>
/// <param name="Credential">
/// The credential exactly as <c>PublicKeyCredential.toJSON()</c> produced it,
/// nested rather than flattened so the framework's parser receives the object
/// it expects untouched. Nothing here is inspected by hand: it goes straight to
/// the passkey handler, which is the only thing in the process qualified to
/// decide whether a signature is valid.
/// </param>
/// <param name="Name">
/// An optional label for the device, shown when listing an account's passkeys.
/// Purely cosmetic and never trusted for anything.
/// </param>
internal sealed record PasskeyCredentialRequest(JsonElement? Credential, string? Name);

/// <summary>Confirms a newly enrolled passkey.</summary>
internal sealed record PasskeyRegisteredResponse(
    string CredentialId,
    string? Name,
    DateTimeOffset CreatedAt);

/// <summary>
/// The outcome of a passkey sign-in attempt.
/// </summary>
/// <param name="Status">
/// <c>authenticated</c> when a session cookie was issued, or <c>mfaRequired</c>
/// when the account has a second factor and the caller must now post a code to
/// <c>/api/auth/mfa/totp/verify</c>.
/// </param>
/// <param name="User">
/// Present only alongside <c>authenticated</c>. A <c>mfaRequired</c> response
/// deliberately carries no account detail whatsoever: the caller has proved
/// possession of one factor, which is not yet permission to read anything.
/// </param>
internal sealed record SignInResponse(string Status, CurrentUserResponse? User)
{
    public static SignInResponse MfaRequired { get; } = new("mfaRequired", null);

    public static SignInResponse Authenticated(CurrentUserResponse user) =>
        new("authenticated", user);
}

/// <summary>Starts TOTP enrolment.</summary>
/// <param name="SharedKey">
/// The authenticator secret, formatted in groups for manual entry.
/// </param>
/// <param name="AuthenticatorUri">
/// The <c>otpauth://</c> URI to render as a QR code.
/// </param>
/// <remarks>
/// Returned once, to an already authenticated caller, over TLS. It is not
/// stored anywhere client-side by the server's doing and it is not returned
/// again: a second enrolment call issues a fresh secret and invalidates this
/// one.
/// </remarks>
internal sealed record TotpEnrollmentResponse(string SharedKey, string AuthenticatorUri);

/// <summary>Supplies a six-digit code from the authenticator app.</summary>
internal sealed record TotpVerifyRequest(string? Code);

/// <summary>Confirms that two-factor authentication is now switched on.</summary>
/// <param name="RecoveryCodes">
/// Single-use codes that stand in for the authenticator if the device is lost.
/// Shown exactly once, here. They are stored hashed and cannot be redisplayed;
/// a caller who loses them asks for a fresh set, which invalidates these.
/// </param>
internal sealed record TotpEnabledResponse(string Status, IReadOnlyList<string> RecoveryCodes);

/// <summary>One of the account's registered passkeys.</summary>
/// <param name="Id">
/// The credential identifier, base64url, exactly as WebAuthn spells it
/// everywhere else. It is what a revocation request names.
/// </param>
/// <param name="Name">
/// The label the account holder gave the device, or null if they gave none.
/// Cosmetic, chosen by the account holder, and never trusted for anything.
/// </param>
/// <remarks>
/// There is deliberately no "last used" timestamp. The framework's passkey
/// record does not keep one, and a field invented here would either be a lie or
/// a second write on the sign-in path for the sake of a caption.
/// </remarks>
internal sealed record PasskeySummary(string Id, string? Name, DateTimeOffset CreatedAt);

/// <summary>Who the caller is.</summary>
/// <param name="Passkeys">
/// The account's credentials, rather than a count of them.
/// </param>
/// <remarks>
/// This used to be a count, on the reasoning that the identifiers were not
/// needed by anything. Revocation changed that: an account area that can only
/// add passkeys and never remove one leaves somebody who has lost a device with
/// no way to cut it off, and removing one means being able to name it. The
/// identifiers are not secret — the authenticator and the browser both hold
/// them already — and they are only ever disclosed to the account holder.
/// </remarks>
/// <param name="AuthenticationMethod">
/// How this session was established: <c>passkey</c>, <c>totp</c> or
/// <c>email</c>. Null only for a session issued before the field existed.
/// </param>
/// <param name="StrongAuthentication">
/// Whether that method counts as a second factor. Derived from
/// <paramref name="AuthenticationMethod"/> and sent anyway, so the front end
/// never has to keep its own copy of which methods qualify.
/// </param>
/// <param name="SecondFactorRequired">
/// Whether this account's roles oblige it to sign in with a passkey or an
/// authenticator app. True for Contributor and Administrator, false for
/// everybody else. It says nothing about whether the obligation is currently
/// met; the passkey list and the two-factor flag above answer that.
/// </param>
internal sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool TwoFactorEnabled,
    IReadOnlyList<PasskeySummary> Passkeys,
    string? AuthenticationMethod,
    bool StrongAuthentication,
    bool SecondFactorRequired);

/// <summary>
/// A proof-of-work challenge, to be solved and sent back with the request it
/// pays for.
/// </summary>
/// <param name="Salt">
/// Thirty-two hex characters. The client hashes <c>{salt}:{counter}</c> for
/// increasing counters until the digest opens with <paramref name="Difficulty"/>
/// zero bits.
/// </param>
/// <param name="Difficulty">
/// Leading zero <em>bits</em>, not hex characters. A client that counts
/// characters will solve four times too little or sixteen times too much.
/// </param>
/// <param name="ExpiresAt">
/// ISO-8601. A string rather than a <see cref="DateTimeOffset"/>, and the one
/// field in this API that is deliberately not a date on the wire: it is covered
/// by <paramref name="Signature"/>, so it has to come back exactly as it was
/// sent, and a client that parses and re-formats it will produce a different
/// string and be refused. Echo it verbatim.
/// </param>
/// <param name="Signature">
/// Proves the server issued this challenge. Opaque to the client, which must
/// return it unchanged.
/// </param>
internal sealed record ChallengeResponse(
    string Salt,
    int Difficulty,
    string ExpiresAt,
    string Signature);

/// <summary>Asks for a one-time code to be emailed.</summary>
/// <param name="Email">The address to send it to.</param>
internal sealed record SignInCodeRequest(string? Email);

/// <summary>
/// The only answer the code request ever gives.
/// </summary>
/// <param name="ResendAfterSeconds">
/// How long the front end should wait before re-enabling its resend control.
/// A fixed value read from configuration, deliberately not computed from what
/// this address has recently been sent — a countdown that varied would tell an
/// unauthenticated caller whether somebody had just asked for a code for that
/// address.
/// </param>
/// <param name="ExpiresInSeconds">
/// How long a code lasts, for the copy on the entry screen. Also a fixed
/// configured value, and also not a fact about any particular code.
/// </param>
internal sealed record SignInCodeRequestedResponse(
    string Status,
    string Message,
    int ResendAfterSeconds,
    int ExpiresInSeconds)
{
    /// <summary>How many digits a code has.</summary>
    public const int CodeLength = 6;

    public static SignInCodeRequestedResponse For(Sw5e.Identity.Sw5eIdentityOptions options) => new(
        "pending",
        "If that address can be signed in to, a code is on its way. Check the inbox, " +
        "including the spam folder.",
        (int)options.EmailSignInCodeResendCooldown.TotalSeconds,
        (int)options.EmailSignInCodeLifetime.TotalSeconds);
}

/// <summary>Redeems an emailed code.</summary>
/// <param name="Email">
/// The address the code was sent to. Required, and checked: the address is part
/// of what the stored hash covers, so a code issued for one address cannot be
/// redeemed against another.
/// </param>
/// <param name="Code">The digits from the message.</param>
internal sealed record SignInCodeVerifyRequest(string? Email, string? Code);

/// <summary>Confirms that a passkey is no longer registered.</summary>
internal sealed record PasskeyRemovedResponse(string Status)
{
    public static PasskeyRemovedResponse Removed { get; } = new("removed");
}

/// <summary>Replaces the set of roles held by one account.</summary>
/// <param name="Roles">
/// The roles the account should hold afterwards, drawn from
/// <c>Sw5eRoles.Assignable</c>. Absent roles are revoked, so this is a
/// declaration of the desired state rather than an increment — which means a
/// replayed request cannot accumulate privilege.
/// </param>
internal sealed record AssignRolesRequest(
    [property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles);

/// <summary>The roles an account holds after an administrative change.</summary>
/// <param name="AwaitingSecondFactor">
/// True when the account now holds an elevated role and has neither a passkey
/// nor an authenticator app, so it cannot yet use what it has been given.
/// </param>
/// <remarks>
/// That last field is the difference between a grant that works and one that
/// looks like it worked. Contributor and Administrator can only be exercised
/// from a session established with a passkey or an authenticator code, so
/// granting one to somebody who has neither hands them a role and no way to
/// use it. Rather than refuse the grant — which would make the administrator's
/// action fail for a reason about somebody else's device — or quietly relax the
/// rule, the grant succeeds, the account is emailed and told to enrol, and this
/// flag lets the administrator see the same thing on screen.
/// </remarks>
internal sealed record AccountRolesResponse(
    Guid UserId,
    IReadOnlyList<string> Roles,
    bool AwaitingSecondFactor);

/* ------------------------------------------------- the administrative surface */

/// <summary>
/// One account as an administrator sees it in the directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>The address is here, and that is the point.</b> Every other response on
/// this platform withholds it: the flag queue shows a display name to
/// Contributors precisely so that being trusted with content does not entitle
/// somebody to the address of everyone who ever reported a typo. This one does
/// not withhold it, because the task it exists for is "somebody wrote to me
/// asking to be made a contributor, find them", and an address is the only
/// identifier that person and the administrator share.
/// </para>
/// <para>
/// That is exactly why the route behind it is administrators only, from a
/// session that used a passkey or an authenticator, and why there is no
/// variant of it for anybody else. This record is a directory of real people's
/// email addresses; the authorization on the route is the only thing standing
/// between it and being a mailing list.
/// </para>
/// <para>
/// What is still absent, on an administrative route and with an administrator
/// reading it: the security stamp, the concurrency stamp, the normalised
/// identifiers, and the password hash column that no flow here ever writes.
/// None of them tells an administrator anything they can act on, and all of
/// them help somebody holding a stolen administrator session reason about
/// tokens.
/// </para>
/// </remarks>
/// <param name="SecondFactorEnrolled">
/// Whether the account holds a passkey or an authenticator app — the thing an
/// elevated role cannot be exercised without. Present so that an administrator
/// can see, before granting Contributor, whether the grant will be usable; the
/// alternative is finding out afterwards from the response's
/// <c>awaitingSecondFactor</c> flag.
/// </param>
/// <param name="LockedOut">
/// Whether failed attempts have currently locked the account out. Distinct from
/// <paramref name="Suspension"/> in every way that matters: this one is the
/// framework counting failures and it expires by itself.
/// </param>
internal sealed record AdminUserSummary(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool EmailConfirmed,
    bool TwoFactorEnabled,
    bool SecondFactorEnrolled,
    bool LockedOut,
    AccountSuspensionResponse? Suspension,
    DateTimeOffset CreatedAt);

/// <summary>A page of the account directory.</summary>
/// <remarks>
/// The same envelope the flag queue uses, deliberately. Two paged collections
/// on one platform with two different shapes is two client-side pagers, and the
/// second one is always the one with the off-by-one.
/// </remarks>
internal sealed record AdminUserListResponse(
    IReadOnlyList<AdminUserSummary> Users,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

/// <summary>
/// An account's suspension, as an administrator sees it.
/// </summary>
/// <param name="Reason">
/// The administrator's own words. Disclosed here, on an administrators-only
/// route, and never to the account it is about — see <c>AccountSuspension</c>.
/// </param>
/// <param name="ByUserId">
/// Who did it. An identifier rather than a name, because the name is a fact
/// about an account that may itself have been deleted since; the administrative
/// log is where the readable version lives, with the name copied onto the row.
/// </param>
internal sealed record AccountSuspensionResponse(
    DateTimeOffset At,
    string? Reason,
    Guid? ByUserId);

/// <summary>
/// One account in administrative detail.
/// </summary>
/// <param name="OutstandingDrafts">
/// How many unpublished drafts this account owns. It is here because it is the
/// one thing that will refuse a deletion, and an administrator meeting that
/// refusal for the first time at the moment they try to delete somebody is an
/// administrator who has to go and find out what a draft is. Null when the
/// deployment serves content from files and therefore has no authoring at all.
/// </param>
internal sealed record AdminUserDetail(
    AdminUserSummary User,
    int? OutstandingDrafts);

/// <summary>Declares whether an account should be suspended.</summary>
/// <param name="Suspended">
/// The state the account should end up in. Declarative, like the role
/// assignment beside it: a replayed request cannot deepen a suspension, and
/// "reinstate" is the same endpoint with a different value rather than a second
/// route somebody has to remember exists.
/// </param>
/// <param name="Reason">
/// Why. Required when suspending — a suspension nobody wrote a reason for is
/// one nobody can review — and refused when reinstating, because there is
/// nowhere for it to be stored and silently discarding it would be worse.
/// </param>
internal sealed record SetSuspensionRequest(bool? Suspended, string? Reason);

/// <summary>The state of an account after a suspension was set or lifted.</summary>
internal sealed record AccountSuspensionStateResponse(
    Guid UserId,
    AccountSuspensionResponse? Suspension);

/// <summary>Confirms that an account no longer exists.</summary>
/// <param name="AuthorshipRetained">
/// Always true, and sent anyway. Deleting an account does not delete what it
/// wrote: revisions and reports keep the identifier and render as a removed
/// account. Saying so in the response is how the administrator who pressed the
/// button finds that out at the moment it matters, rather than from a
/// paragraph in a document they read once.
/// </param>
internal sealed record AccountDeletedResponse(
    Guid UserId,
    bool AuthorshipRetained)
{
    public static AccountDeletedResponse For(Guid userId) => new(userId, true);
}

/// <summary>One entry in the administrative log.</summary>
/// <remarks>
/// The display names are the copies stored on the row rather than a lookup, so
/// an entry stays legible after either account has gone. See
/// <c>AdministrativeAction</c>.
/// </remarks>
internal sealed record AdministrativeActionResponse(
    Guid Id,
    string Action,
    Guid ActorUserId,
    string ActorDisplayName,
    Guid SubjectUserId,
    string SubjectDisplayName,
    IReadOnlyList<string>? RolesBefore,
    IReadOnlyList<string>? RolesAfter,
    string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>A page of the administrative log.</summary>
internal sealed record AdministrativeLogResponse(
    IReadOnlyList<AdministrativeActionResponse> Actions,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

/// <summary>
/// The optional note recorded against a deletion.
/// </summary>
/// <remarks>
/// A body on a <c>DELETE</c>, which is unusual and is the right place for it
/// anyway: the alternative is a query parameter, and a sentence naming a person
/// and describing their conduct does not belong in a URL that every access log
/// between the browser and the process writes down.
/// </remarks>
internal sealed record DeleteAccountRequest(string? Reason);
