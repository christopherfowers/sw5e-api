namespace Sw5e.Email;

/// <summary>
/// One transactional message, expressed in terms no provider owns.
/// </summary>
/// <remarks>
/// <para>
/// This is the currency of <see cref="IEmailSender"/>. It carries only what
/// every sending path can express — MailerSend's JSON body, an RFC 5322
/// message over SMTP, and the in-memory capture used in development all
/// represent exactly these fields — so adding a provider never means widening
/// the type, and no caller ever learns which provider is configured.
/// </para>
/// <para>
/// Deliberately absent: CC, BCC, attachments, and MailerSend's server-side
/// templates and personalisation. Every one of those is either not needed by
/// the account flows this exists for, or actively unwanted (see
/// <see cref="To"/>), and each would be a feature the SMTP adapter would have
/// to emulate or refuse. The seam holds because the contract is narrow. When a
/// genuine need for one appears, add it to every adapter in the same change.
/// </para>
/// <para>
/// The invariants are enforced in the constructor rather than checked by each
/// adapter, so an instance that exists is an instance that can be sent.
/// </para>
/// </remarks>
public sealed class EmailMessage
{
    /// <summary>
    /// RFC 5322 caps a header line at 998 octets, and MailerSend documents the
    /// same number for <c>subject</c>. Nothing longer survives the trip intact.
    /// </summary>
    public const int MaxSubjectLength = 998;

    /// <summary>Builds a message, validating every part of it.</summary>
    /// <param name="from">
    /// The sending mailbox. For MailerSend this must belong to a domain
    /// verified on the account, or the API answers 422.
    /// </param>
    /// <param name="to">The single recipient. See <see cref="To"/>.</param>
    /// <param name="subject">The subject line.</param>
    /// <param name="plainTextBody">
    /// The <c>text/plain</c> alternative. Required, never optional: a
    /// text-only client, a screen reader and a spam filter all read this part,
    /// and an HTML-only transactional email scores badly with the last of
    /// those.
    /// </param>
    /// <param name="htmlBody">The <c>text/html</c> alternative.</param>
    /// <param name="replyTo">
    /// Where a reply should go, when that is not the sending mailbox. Account
    /// mail is typically sent from a no-reply address with this pointed at
    /// somewhere a human reads.
    /// </param>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The subject is blank, too long, or contains a control character; or
    /// either body is blank.
    /// </exception>
    public EmailMessage(
        EmailAddress from,
        EmailAddress to,
        string subject,
        string plainTextBody,
        string htmlBody,
        EmailAddress? replyTo = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(plainTextBody);
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlBody);

        if (subject.Length > MaxSubjectLength)
        {
            throw new ArgumentException(
                $"A subject may not exceed {MaxSubjectLength} characters.",
                nameof(subject));
        }

        // The subject is a header, so it splits on CR or LF exactly the way an
        // address does — and unlike an address it is the field most likely to
        // contain something a user typed. EmailAddress rejects the whole
        // control range for the same reason; see the note there.
        foreach (var c in subject)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException(
                    "A subject may not contain control characters.",
                    nameof(subject));
            }
        }

        From = from;
        To = to;
        Subject = subject;
        PlainTextBody = plainTextBody;
        HtmlBody = htmlBody;
        ReplyTo = replyTo;
    }

    /// <summary>The sending mailbox.</summary>
    public EmailAddress From { get; }

    /// <summary>
    /// The one and only recipient.
    /// </summary>
    /// <remarks>
    /// Singular by design, not by omission. Every message this subsystem
    /// exists to send — verify your address, reset your password — carries a
    /// bearer token in its body, and a bearer token is exactly as valuable as
    /// the account it unlocks. A collection here would make "reset link
    /// delivered to two people" a plausible bug; a single address makes it
    /// unrepresentable. A future bulk or announcement path is a different
    /// contract, not a wider version of this one.
    /// </remarks>
    public EmailAddress To { get; }

    /// <summary>Where replies should be directed, if not <see cref="From"/>.</summary>
    public EmailAddress? ReplyTo { get; }

    /// <summary>The subject line.</summary>
    public string Subject { get; }

    /// <summary>The <c>text/plain</c> alternative.</summary>
    public string PlainTextBody { get; }

    /// <summary>The <c>text/html</c> alternative.</summary>
    public string HtmlBody { get; }
}
