namespace Sw5e.Email;

/// <summary>
/// The seam. Everything that sends mail depends on this and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// One method, one argument, one return value — because the whole point of the
/// abstraction is that adding a provider is a new class plus a configuration
/// change, and never an edit to calling code. Every concept a specific
/// provider owns (API tokens, base addresses, SMTP hosts, TLS modes, HTTP
/// status codes, SMTP reply codes) lives below this line. Every concept a
/// caller owns (who, what, and did it work) lives above it.
/// </para>
/// <para>
/// The implementations in this assembly are:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>MailerSendEmailSender</c> — the production provider, over their
///     HTTP API.
///   </description></item>
///   <item><description>
///     <c>SmtpEmailSender</c> — any RFC 5321 relay. This one exists to keep
///     the abstraction honest: an interface with a single implementation has
///     never actually been shown to abstract anything, because the first thing
///     to leak is always a detail the sole implementation happened to expose.
///     Two implementations that a test exercises through identical calling
///     code is the proof.
///   </description></item>
///   <item><description>
///     <c>CapturingEmailSender</c> — records instead of sending, so the app
///     runs locally with no credentials and tests can assert on what would
///     have gone out.
///   </description></item>
///   <item><description>
///     <c>RetryingEmailSender</c> — a decorator, not a provider. Because
///     retry is expressed against <see cref="EmailFailureKind"/> rather than
///     against any provider's error vocabulary, every provider inherits the
///     same resilience for free and a new one gets it without writing any.
///   </description></item>
/// </list>
/// <para>
/// <b>Contract for implementers.</b> A delivery problem is reported by
/// returning a failed <see cref="EmailDeliveryResult"/>, never by throwing:
/// callers steer on the result, and an implementation that throws instead
/// bypasses the retry decorator entirely. Exceptions are reserved for
/// programmer error. The one exception to that rule is cancellation —
/// <see cref="OperationCanceledException"/> from the caller's own token must
/// propagate, because a cancelled request is not a failed send and must not be
/// retried.
/// </para>
/// <para>
/// Implementations are registered as singletons and must be safe for
/// concurrent use.
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>Hands one message to the configured provider.</summary>
    /// <param name="message">The message. Already validated by construction.</param>
    /// <param name="cancellationToken">
    /// Cancels the send. Note that cancellation after the provider has accepted
    /// the message does not un-send it.
    /// </param>
    /// <returns>
    /// Whether the provider accepted the message, and why not if it did not.
    /// <b>Inspect this.</b> Discarding it turns every delivery failure into
    /// silence — see <see cref="EmailDeliveryResult"/>.
    /// </returns>
    Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default);
}
