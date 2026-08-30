namespace Sw5e.Email.Accounts;

/// <summary>
/// The account-flow messages, ready to send. This is the contract the identity
/// system depends on.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately two methods rather than a general "send a templated
/// email" facility, because a general facility would push the choice of
/// wording, subject line, sender identity and HTML onto every caller, and the
/// wording of a password-reset email is not something each call site should be
/// deciding for itself. What the caller owns is the token and the URL built
/// from it; everything else is this library's problem.
/// </para>
/// <para>
/// Correspondingly, what is <b>not</b> here: token generation, token
/// validation, expiry enforcement, and URL construction. Those belong to the
/// identity system, which is the only thing that can do them correctly. This
/// library never sees a token in isolation and never decides whether one is
/// still good — <paramref name="validFor"/> below is copy for the reader, not
/// a policy this library enforces.
/// </para>
/// <para>
/// Both methods return rather than throw on delivery failure; see
/// <see cref="EmailDeliveryResult"/>. A caller that ignores the result has
/// written an account flow that silently swallows undeliverable mail.
/// </para>
/// <para>Registered as a singleton and safe for concurrent use.</para>
/// </remarks>
public interface IAccountEmailService
{
    /// <summary>
    /// Asks the recipient to prove they control the address they registered.
    /// </summary>
    /// <param name="recipient">
    /// The address being verified, with the account holder's display name when
    /// one is known — it is used in the greeting, and it is treated as
    /// untrusted input throughout.
    /// </param>
    /// <param name="verificationUrl">
    /// The absolute <c>https</c> URL that completes verification, token and
    /// all. Built by the caller, because only the caller knows the token and
    /// the front end's route.
    /// </param>
    /// <param name="validFor">
    /// How long the link remains usable, if the caller wants the reader told.
    /// Rendered as a sentence in both parts and rounded to a whole unit; null
    /// falls back to wording that makes no claim about time.
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="verificationUrl"/> is not an absolute <c>http</c> or
    /// <c>https</c> URL.
    /// </exception>
    Task<EmailDeliveryResult> SendEmailVerificationAsync(
        EmailAddress recipient,
        string verificationUrl,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the link that lets someone locked out of an account choose a new
    /// password.
    /// </summary>
    /// <param name="recipient">
    /// The account's address, with a display name when one is known.
    /// </param>
    /// <param name="resetUrl">
    /// The absolute <c>https</c> URL that completes the reset, token and all.
    /// </param>
    /// <param name="validFor">
    /// How long the link remains usable, if the caller wants the reader told.
    /// Worth supplying here: a reset link that has quietly expired is the most
    /// common reason a locked-out user gives up.
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="resetUrl"/> is not an absolute <c>http</c> or
    /// <c>https</c> URL.
    /// </exception>
    Task<EmailDeliveryResult> SendPasswordResetAsync(
        EmailAddress recipient,
        string resetUrl,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default);
}
