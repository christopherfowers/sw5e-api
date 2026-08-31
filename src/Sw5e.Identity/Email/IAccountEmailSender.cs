namespace Sw5e.Identity.Email;

/// <summary>
/// Delivers the three messages the account flows depend on.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an interface with no implementation in this assembly. Mail
/// delivery is somebody else's problem — <c>Sw5e.Email</c> owns the providers —
/// and the account code must not grow an opinion about SMTP, API keys or
/// retries. What it does own is the shape of the contract, because the security
/// properties live in that shape.
/// </para>
/// <para>
/// Note what the methods are <em>not</em>: there is no
/// <c>SendPasswordResetLink</c>, because there are no passwords. The framework's
/// own <c>IEmailSender&lt;TUser&gt;</c> is modelled on password accounts and
/// would have forced a passkey enrolment link to travel under the name
/// "password reset", which is exactly the kind of small lie that later gets a
/// flow wired to the wrong template.
/// </para>
/// <para>
/// Every method takes an already-built absolute URL. Building the link is the
/// API's job because only the API knows its own public origin; the mail
/// provider must never be in a position to decide where a verification link
/// points.
/// </para>
/// <para>
/// Implementations must treat a failure as a failure and throw. Swallowing a
/// delivery error turns "we emailed you a link" into a lie the user cannot
/// distinguish from a slow inbox.
/// </para>
/// </remarks>
public interface IAccountEmailSender
{
    /// <summary>
    /// Invites a brand-new, unverified account to prove it controls the
    /// address.
    /// </summary>
    Task SendEmailVerificationAsync(
        AccountEmailRecipient recipient,
        string verificationUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an already-verified account a link that lets it enrol a new
    /// passkey — the recovery path for someone who lost every device they had
    /// registered.
    /// </summary>
    /// <remarks>
    /// This is what a registration attempt against an existing address
    /// produces. The registration endpoint cannot say "that address is already
    /// taken" without confirming to a stranger that the address has an account
    /// here, so it says nothing and mails the account holder instead. The
    /// message must therefore read sensibly to somebody who did not ask for it,
    /// and must say plainly that ignoring it is safe.
    /// </remarks>
    Task SendPasskeyRecoveryAsync(
        AccountEmailRecipient recipient,
        string recoveryUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells an account that a security-relevant change just happened to it:
    /// a new passkey enrolled, a passkey removed, two-factor turned on, roles
    /// changed.
    /// </summary>
    /// <remarks>
    /// This is the notification that turns a silent account takeover into a
    /// loud one. It is sent after the fact and never carries a link that
    /// changes anything.
    /// </remarks>
    Task SendSecurityNoticeAsync(
        AccountEmailRecipient recipient,
        string summary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the short numeric code that signs somebody in without a passkey.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only message on this interface that carries a credential in its
    /// body rather than a link. Implementations must treat it as one: the code
    /// goes into the message and nowhere else, and in particular it never
    /// reaches a log. The capture provider logs recipients and subjects and
    /// deliberately not bodies, which is what makes that rule hold for
    /// development and for the tests as well as for production.
    /// </para>
    /// <para>
    /// It is also the only message here that is worth phrasing for a reader who
    /// did not ask for it. A code arriving unrequested means somebody typed
    /// this address into the sign-in form, so the message has to say plainly
    /// that ignoring it costs nothing.
    /// </para>
    /// </remarks>
    /// <param name="code">
    /// The digits, exactly as the reader will type them back.
    /// </param>
    /// <param name="validFor">How long the code works.</param>
    Task SendSignInCodeAsync(
        AccountEmailRecipient recipient,
        string code,
        TimeSpan validFor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells an address with no account here that somebody tried to sign in
    /// with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because of what the sign-in endpoint must not do. It cannot
    /// answer differently for an address that has an account and one that does
    /// not, or it becomes a way to test whether a given person has an account
    /// here. It cannot even take a measurably different amount of time. So both
    /// branches send exactly one message, and this is the one the unknown
    /// branch sends.
    /// </para>
    /// <para>
    /// The result is that a probe turns into a note to the actual owner of the
    /// address, which is the same trade the registration endpoint already
    /// makes. It carries no code and no link that changes anything, because
    /// there is no account for it to change.
    /// </para>
    /// </remarks>
    Task SendUnknownAddressSignInNoticeAsync(
        string emailAddress,
        CancellationToken cancellationToken = default);
}

/// <summary>Who a message is going to.</summary>
/// <param name="EmailAddress">The verified or claimed address.</param>
/// <param name="DisplayName">The name to greet them by.</param>
public readonly record struct AccountEmailRecipient(string EmailAddress, string DisplayName);
