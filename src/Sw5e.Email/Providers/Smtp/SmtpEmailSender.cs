using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sw5e.Email.Providers.Smtp;

/// <summary>
/// Sends through any RFC 5321 submission relay.
/// </summary>
/// <remarks>
/// <para>
/// This adapter earns its place twice over. It is a real production option —
/// every mail provider worth using offers SMTP submission, so a provider
/// outage or a commercial disagreement is survivable by changing configuration
/// rather than by writing code. And it is the evidence that the abstraction
/// above it is genuine: a MailerSend-shaped interface with one MailerSend
/// implementation would be a wrapper claiming to be a seam. Two
/// implementations with nothing in common below <see cref="IEmailSender"/> —
/// one JSON over HTTP, one a stateful text protocol over a socket — driven by
/// identical calling code, is a seam that has been tested.
/// </para>
/// <para>
/// Built on <see cref="SmtpClient"/> from the framework rather than on a
/// third-party client. It handles submission, STARTTLS and <c>AUTH LOGIN</c>,
/// which is the entire requirement, and it costs no dependency. Its one
/// relevant limitation — no implicit TLS on port 465 — is documented on
/// <see cref="SmtpOptions.UseStartTls"/> and rejected at startup rather than
/// discovered at runtime.
/// </para>
/// </remarks>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptions<SmtpOptions> _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    /// <summary>Creates the adapter.</summary>
    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = _options.Value;

        // A client per send, deliberately. SmtpClient holds one connection and
        // permits exactly one send in flight; sharing an instance across
        // concurrent requests raises InvalidOperationException. Pooling it
        // safely would mean a lock, which would serialise every outgoing email
        // in the process. Connection setup is the cost of correctness here.
        using var client = new SmtpClient(options.Host, options.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = options.UseStartTls,

            // SmtpClient's own timeout covers the synchronous path; the linked
            // token below is what actually bounds the asynchronous one. Both
            // are set so neither path can run unbounded.
            Timeout = (int)options.Timeout.TotalMilliseconds,

            // Explicit rather than relying on the default: the alternative is
            // the process identity's Windows credentials, which is never what
            // is wanted for an internet relay and would be a confusing thing to
            // debug if it ever became the default.
            UseDefaultCredentials = false,
            Credentials = BuildCredentials(options),
        };

        using var mail = BuildMailMessage(message);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        try
        {
            await client.SendMailAsync(mail, timeout.Token).ConfigureAwait(false);

            _logger.LogDebug(
                "The SMTP relay at {Host}:{Port} accepted a message for {Recipient}.",
                options.Host,
                options.Port,
                message.To.Address);

            // SMTP issues no message identifier the client can see — the
            // relay's own Message-ID is assigned server side and never comes
            // back over the wire. Null is the honest answer.
            return EmailDeliveryResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. Not a delivery failure; must not be retried.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The linked timeout fired instead, so the relay was too slow.
            _logger.LogWarning(
                "The SMTP relay at {Host}:{Port} did not complete within {Timeout}.",
                options.Host,
                options.Port,
                options.Timeout);

            return EmailDeliveryResult.Transient(
                $"The SMTP relay did not complete the submission within {options.Timeout}.");
        }
        catch (SmtpException exception)
        {
            return Classify(exception, message, options);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            // The connection died mid-conversation, below the level at which
            // SmtpClient produces a reply code.
            _logger.LogWarning(
                exception,
                "The connection to the SMTP relay at {Host}:{Port} failed.",
                options.Host,
                options.Port);

            return EmailDeliveryResult.Transient(
                $"The connection to the SMTP relay failed: {exception.Message}");
        }
    }

    private static ICredentialsByHost? BuildCredentials(SmtpOptions options) =>
        string.IsNullOrEmpty(options.UserName)
            ? null
            : new NetworkCredential(options.UserName, options.Password);

    /// <summary>
    /// Builds the RFC 5322 message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both bodies go on as alternate views rather than one going in
    /// <see cref="MailMessage.Body"/>, which is what produces a proper
    /// <c>multipart/alternative</c>. Order is significant and is not
    /// stylistic: a client picks the <i>last</i> part it can render, so plain
    /// text must come first and HTML second. Reversed, every graphical client
    /// shows the plain-text version.
    /// </para>
    /// <para>
    /// Everything is UTF-8, and the subject encoding is set explicitly. A
    /// subject left to the default is encoded per the ambient culture, which in
    /// this application's containers is invariant — the practical effect being
    /// that a name with an accent in it arrives as mojibake.
    /// </para>
    /// </remarks>
    private static MailMessage BuildMailMessage(EmailMessage message)
    {
        var mail = new MailMessage
        {
            From = ToMailAddress(message.From),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8,
        };

        try
        {
            mail.To.Add(ToMailAddress(message.To));

            if (message.ReplyTo is not null)
            {
                mail.ReplyToList.Add(ToMailAddress(message.ReplyTo));
            }

            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                message.PlainTextBody, Encoding.UTF8, MediaTypeNames.Text.Plain));

            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                message.HtmlBody, Encoding.UTF8, MediaTypeNames.Text.Html));
        }
        catch
        {
            // The message owns the views once they are attached, so anything
            // that fails part-way through construction has to clean up rather
            // than leak the streams behind the views already added.
            mail.Dispose();
            throw;
        }

        return mail;
    }

    private static MailAddress ToMailAddress(EmailAddress address) =>
        address.DisplayName is null
            ? new MailAddress(address.Address)
            : new MailAddress(address.Address, address.DisplayName, Encoding.UTF8);

    /// <summary>
    /// Maps an SMTP reply code onto the transient/permanent decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SMTP makes this unusually easy, because RFC 5321 defines the first digit
    /// to mean exactly this: <c>4yz</c> is a temporary failure the sender
    /// should retry, <c>5yz</c> is permanent and must not be. So a mailbox
    /// that is full (452) is retried and a mailbox that does not exist (550) is
    /// not, with no per-relay knowledge needed.
    /// </para>
    /// <para>
    /// Authentication failures land in the 5xx range (535) and are therefore
    /// permanent, which is right: a rejected password does not become correct
    /// on the second attempt. They are logged at error level because, exactly
    /// like a rejected MailerSend token, nothing at all can be sent until
    /// somebody fixes the configuration.
    /// </para>
    /// <para>
    /// <see cref="SmtpStatusCode.GeneralFailure"/> is the framework's own
    /// value for "no reply code was obtained" — a refused connection, a failed
    /// TLS handshake, a name that would not resolve. Those are transient, so
    /// anything without a recognisable reply code falls to that bucket.
    /// </para>
    /// </remarks>
    private EmailDeliveryResult Classify(
        SmtpException exception,
        EmailMessage message,
        SmtpOptions options)
    {
        // SmtpFailedRecipientException derives from SmtpException and carries
        // the per-recipient reply code, which for a single-recipient message is
        // the one that matters. StatusCode is already the right value on both.
        var status = (int)exception.StatusCode;

        var reason = string.Format(
            CultureInfo.InvariantCulture,
            "The SMTP relay returned {0} ({1}). {2}",
            status,
            exception.StatusCode,
            exception.Message);

        if (status >= 500 && status < 600)
        {
            if (status == 530 || status == 535)
            {
                _logger.LogError(
                    exception,
                    "The SMTP relay at {Host}:{Port} rejected the configured credentials. " +
                    "No mail can be sent until Email__Smtp__UserName and Email__Smtp__Password " +
                    "are corrected.",
                    options.Host,
                    options.Port);
            }
            else
            {
                _logger.LogError(
                    exception,
                    "The SMTP relay permanently rejected a message for {Recipient}. {Reason}",
                    message.To.Address,
                    reason);
            }

            return EmailDeliveryResult.Permanent(reason);
        }

        _logger.LogWarning(
            exception,
            "The SMTP relay temporarily refused a message for {Recipient}. {Reason}",
            message.To.Address,
            reason);

        return EmailDeliveryResult.Transient(reason);
    }
}
