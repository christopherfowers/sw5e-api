using System.Globalization;
using Microsoft.Extensions.Options;
using Sw5e.Email.Configuration;

namespace Sw5e.Email.Accounts;

/// <summary>
/// Renders the account-flow templates and hands the result to whichever
/// provider is configured.
/// </summary>
/// <remarks>
/// This type knows about templates and sender identity. It knows nothing about
/// MailerSend, SMTP, HTTP or retries — it depends on <see cref="IEmailSender"/>
/// and therefore works unchanged against every provider, which is the property
/// the whole design exists to protect.
/// </remarks>
public sealed class AccountEmailService : IAccountEmailService
{
    /// <summary>
    /// Stands in for a display name when the account has none. Second person
    /// and lowercase, so "Hi there," reads as a greeting rather than as a
    /// failed substitution — which is exactly what an empty greeting looks
    /// like to a suspicious reader of a password-reset email.
    /// </summary>
    private const string AnonymousGreeting = "there";

    private readonly IEmailSender _sender;
    private readonly EmailAddress _from;
    private readonly EmailAddress? _replyTo;
    private readonly string _productName;

    /// <summary>
    /// Resolves the sender identity once, at construction.
    /// </summary>
    /// <remarks>
    /// The addresses come out of configuration as strings and are parsed here
    /// rather than per send. Registration has already validated them (see
    /// <see cref="EmailServiceCollectionExtensions"/>), so the throw below is
    /// unreachable through the supported registration path; it is kept because
    /// the alternative — falling back to some default sender — would mean a
    /// typo in configuration silently changes who the mail claims to be from.
    /// </remarks>
    public AccountEmailService(IEmailSender sender, IOptions<EmailOptions> options)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;

        _sender = sender;
        _productName = value.ProductName;

        if (!EmailAddress.TryCreate(value.FromAddress, value.FromName, out var from, out var error))
        {
            throw new EmailConfigurationException(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.FromAddress)}", error);
        }

        _from = from;

        if (!string.IsNullOrWhiteSpace(value.ReplyToAddress))
        {
            if (!EmailAddress.TryCreate(value.ReplyToAddress, null, out var replyTo, out var replyError))
            {
                throw new EmailConfigurationException(
                    $"{EmailOptions.SectionName}:{nameof(EmailOptions.ReplyToAddress)}", replyError);
            }

            _replyTo = replyTo;
        }
    }

    /// <inheritdoc />
    public Task<EmailDeliveryResult> SendEmailVerificationAsync(
        EmailAddress recipient,
        string verificationUrl,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            AccountEmailTemplates.EmailVerification,
            recipient,
            verificationUrl,
            validFor,
            nameof(verificationUrl),
            cancellationToken);

    /// <inheritdoc />
    public Task<EmailDeliveryResult> SendPasswordResetAsync(
        EmailAddress recipient,
        string resetUrl,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            AccountEmailTemplates.PasswordReset,
            recipient,
            resetUrl,
            validFor,
            nameof(resetUrl),
            cancellationToken);

    private Task<EmailDeliveryResult> SendAsync(
        AccountEmailTemplate template,
        EmailAddress recipient,
        string actionUrl,
        TimeSpan? validFor,
        string urlParameterName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProductName"] = _productName,
            ["Greeting"] = recipient.DisplayName ?? AnonymousGreeting,
            ["ActionUrl"] = RequireWebUrl(actionUrl, urlParameterName),
            ["ExpiryNote"] = DescribeExpiry(validFor),
        };

        // The same values, rendered twice with different encoding rules: the
        // HTML part escapes, the text part does not. See EmailTemplate.
        var message = new EmailMessage(
            from: _from,
            to: recipient,
            subject: EmailTemplate.Render(template.Subject, values, htmlEncode: false),
            plainTextBody: EmailTemplate.Render(template.PlainText, values, htmlEncode: false),
            htmlBody: EmailTemplate.Render(template.Html, values, htmlEncode: true),
            replyTo: _replyTo);

        return _sender.SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Insists the action link is an absolute <c>http</c> or <c>https</c> URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This value is written straight into an <c>href</c>. HTML-encoding makes
    /// it safe as <i>markup</i>, but encoding does nothing about the scheme:
    /// <c>javascript:</c>, <c>data:</c> and <c>file:</c> all survive it intact
    /// and all mean something dangerous in the clients that honour them.
    /// Allow-listing the two schemes an account link can legitimately use is
    /// the check that actually matters.
    /// </para>
    /// <para>
    /// Absolute is required for a duller reason as well: a relative URL in an
    /// email resolves against nothing and is simply broken.
    /// </para>
    /// <para>
    /// <c>http</c> is permitted alongside <c>https</c> only so that a local
    /// development front end on <c>http://localhost</c> can be exercised end to
    /// end. Nothing deployed should be producing one.
    /// </para>
    /// </remarks>
    private static string RequireWebUrl(string url, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url, parameterName);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "An account link must be an absolute http or https URL.",
                parameterName);
        }

        return url;
    }

    /// <summary>
    /// Turns a lifetime into the sentence the reader sees.
    /// </summary>
    /// <remarks>
    /// Rounded to a whole unit on purpose. "Expires in 23 hours, 59 minutes and
    /// 58 seconds" is both useless and slightly alarming; the reader is
    /// deciding whether to act now or later, and a unit is enough to decide
    /// that. Sub-minute lifetimes round up to one minute rather than reporting
    /// zero, since "expires in 0 minutes" reads as already expired.
    /// </remarks>
    private static string DescribeExpiry(TimeSpan? validFor)
    {
        if (validFor is null)
        {
            // No claim about time. The single-use property is still true and
            // still worth stating: it is what tells a reader that a link they
            // already followed cannot be reused by someone reading over their
            // shoulder in a forwarded thread.
            return "This link can only be used once.";
        }

        var lifetime = validFor.Value;

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validFor),
                lifetime,
                "A link lifetime must be positive. Sending a link that is already " +
                "expired is never the intent.");
        }

        var (count, unit) = lifetime switch
        {
            { TotalDays: >= 1 } => ((int)Math.Round(lifetime.TotalDays), "day"),
            { TotalHours: >= 1 } => ((int)Math.Round(lifetime.TotalHours), "hour"),
            _ => (Math.Max(1, (int)Math.Ceiling(lifetime.TotalMinutes)), "minute"),
        };

        var plural = count == 1 ? string.Empty : "s";

        return string.Format(
            CultureInfo.InvariantCulture,
            "This link expires in {0} {1}{2} and can only be used once.",
            count,
            unit,
            plural);
    }
}
