namespace Sw5e.Email;

/// <summary>
/// Why a send failed, in the only terms a caller can act on.
/// </summary>
/// <remarks>
/// Provider status codes do not belong above the adapter, but the decision
/// "should this be tried again" does — it is what the retry decorator runs on
/// and what an eventual outbox or dead-letter queue would run on. Each adapter
/// maps its own vocabulary (HTTP status codes for MailerSend, SMTP reply codes
/// for the relay) onto these two answers exactly once.
/// </remarks>
public enum EmailFailureKind
{
    /// <summary>
    /// The send might succeed if repeated: a timeout, a dropped connection, a
    /// 5xx, a rate limit, an SMTP 4xx. Nothing about the message is wrong.
    /// </summary>
    Transient,

    /// <summary>
    /// Repeating the send changes nothing: the address was rejected, the
    /// payload was invalid, the credentials were refused. Retrying burns quota
    /// and delays the caller learning the truth.
    /// </summary>
    Permanent,
}

/// <summary>Detail of a failed send.</summary>
/// <param name="Kind">Whether repeating the send is worth anything.</param>
/// <param name="Reason">
/// A short operator-facing description, safe to log. Adapters must not put
/// credentials or full message bodies in here: this string reaches the
/// application log, which has a wider audience than the mail itself.
/// </param>
/// <param name="RetryAfter">
/// How long the provider asked the caller to wait, when it said so — from a
/// <c>Retry-After</c> header, typically alongside a 429. Null when the provider
/// gave no guidance, which is the normal case.
/// </param>
public sealed record EmailDeliveryFailure(
    EmailFailureKind Kind,
    string Reason,
    TimeSpan? RetryAfter = null);

/// <summary>
/// The outcome of handing a message to a provider.
/// </summary>
/// <remarks>
/// <para>
/// A returned value rather than a thrown exception, because for this operation
/// failure is ordinary. Third-party mail APIs are down, rate-limited and slow
/// as a matter of routine, and a caller that must decide between "tell the user
/// to check their inbox" and "tell the user to try again" should not be
/// steering on exception types. Reserving exceptions for genuine programmer
/// error — a malformed message, missing configuration — keeps them meaningful.
/// </para>
/// <para>
/// The consequence is that <b>the result must be inspected</b>. A caller that
/// discards it has written code that silently drops password-reset emails, and
/// nothing will fail to compile. Consumers are expected to check
/// <see cref="Succeeded"/> and log or surface <see cref="Failure"/>.
/// </para>
/// </remarks>
public sealed class EmailDeliveryResult
{
    private EmailDeliveryResult(string? providerMessageId, EmailDeliveryFailure? failure)
    {
        ProviderMessageId = providerMessageId;
        Failure = failure;
    }

    /// <summary>Whether the provider accepted the message.</summary>
    /// <remarks>
    /// Acceptance is not delivery. MailerSend answers <c>202 Accepted</c> and an
    /// SMTP relay answers <c>250 OK</c>, and both mean "queued": a bounce can
    /// still follow minutes later. This flag is the strongest claim the send
    /// path is able to make.
    /// </remarks>
    public bool Succeeded => Failure is null;

    /// <summary>
    /// The provider's own handle for the message, when it gave one — MailerSend
    /// returns it in the <c>x-message-id</c> response header, and it is the
    /// value to quote when correlating with their activity log or a webhook.
    /// Null for providers that issue no identifier.
    /// </summary>
    public string? ProviderMessageId { get; }

    /// <summary>The failure detail, or null on success.</summary>
    public EmailDeliveryFailure? Failure { get; }

    /// <summary>The provider accepted the message.</summary>
    public static EmailDeliveryResult Success(string? providerMessageId = null) =>
        new(providerMessageId, null);

    /// <summary>The send failed, but repeating it may work.</summary>
    public static EmailDeliveryResult Transient(string reason, TimeSpan? retryAfter = null) =>
        new(null, new EmailDeliveryFailure(EmailFailureKind.Transient, reason, retryAfter));

    /// <summary>The send failed and will keep failing.</summary>
    public static EmailDeliveryResult Permanent(string reason) =>
        new(null, new EmailDeliveryFailure(EmailFailureKind.Permanent, reason));
}
