using Microsoft.Extensions.Options;
using Sw5e.Email;
using Sw5e.Email.Accounts;
using Sw5e.Email.Configuration;
using Sw5e.Identity;
using Sw5e.Identity.Email;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Delivers the identity system's three messages through the platform's email
/// provider.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the two halves, and it exists because they were shaped for
/// different worlds. <see cref="IAccountEmailService"/> offers exactly the two
/// messages a password-based account system needs — verify an address, reset a
/// password — and its verification message is precisely right here. Its reset
/// message is not: this platform has no passwords, so a message telling the
/// reader to choose a new one would be actively wrong, and there is nothing in
/// that contract for the after-the-fact security notice that lets somebody
/// notice a takeover.
/// </para>
/// <para>
/// So verification is delegated to the account email service, which owns the
/// wording, the layout and the sender identity; the other two are composed here
/// and handed to <see cref="IEmailSender"/> directly. That keeps every decision
/// about how mail is delivered — provider, retries, credentials — on the email
/// library's side of the line, while the wording of a passkey-specific message
/// stays with the passkey-specific code that knows what it means.
/// </para>
/// <para>
/// Every path converts a failed delivery into an exception. The email library
/// returns failures rather than throwing, which is the right default for a
/// general-purpose sender; it is the wrong one here, because an account flow
/// that swallows an undeliverable message tells the user to check an inbox
/// nothing will ever arrive in.
/// </para>
/// </remarks>
internal sealed class ProviderAccountEmailSender(
    IAccountEmailService accountEmail,
    IEmailSender sender,
    IOptions<EmailOptions> emailOptions,
    IOptions<Sw5eIdentityOptions> identityOptions) : IAccountEmailSender
{
    private readonly EmailOptions _email = emailOptions.Value;
    private readonly Sw5eIdentityOptions _identity = identityOptions.Value;

    public async Task SendEmailVerificationAsync(
        AccountEmailRecipient recipient,
        string verificationUrl,
        CancellationToken cancellationToken = default)
    {
        var result = await accountEmail.SendEmailVerificationAsync(
            ToAddress(recipient),
            verificationUrl,

            // Told to the reader, and true: the same value bounds the token the
            // link carries. Passing it means the message and the server agree
            // about how long somebody has.
            _identity.EmailTokenLifetime,
            cancellationToken);

        Ensure(result, nameof(SendEmailVerificationAsync));
    }

    public async Task SendPasskeyRecoveryAsync(
        AccountEmailRecipient recipient,
        string recoveryUrl,
        CancellationToken cancellationToken = default)
    {
        // Composed here rather than sent as a password reset. The reader may
        // not have asked for this — a registration attempt against an existing
        // address produces it, which is how this platform avoids confirming to
        // a stranger that an account exists — so the message has to read
        // sensibly to somebody who did not, and has to say plainly that
        // ignoring it is safe.
        const string subject = "Set up a new passkey for your SW5e account";

        var greeting = string.IsNullOrWhiteSpace(recipient.DisplayName)
            ? "Hello,"
            : $"Hello {recipient.DisplayName},";

        var body =
            $"""
             {greeting}

             Someone asked to set up a new passkey for the SW5e account registered
             to this address. If that was you — perhaps because you lost the device
             you signed in with — open the link below within {Describe(_identity.EmailTokenLifetime)}:

             {recoveryUrl}

             If it was not you, you can ignore this message. Nothing has changed,
             and nobody can use the link without access to this mailbox. Your
             existing passkeys keep working either way.
             """;

        await SendAsync(recipient, subject, body, cancellationToken);
    }

    public async Task SendSecurityNoticeAsync(
        AccountEmailRecipient recipient,
        string summary,
        CancellationToken cancellationToken = default)
    {
        const string subject = "A security change was made to your SW5e account";

        var greeting = string.IsNullOrWhiteSpace(recipient.DisplayName)
            ? "Hello,"
            : $"Hello {recipient.DisplayName},";

        // Carries no link, deliberately. This message is sent after something
        // has already happened, and a notification that also offers a one-click
        // action is a notification an attacker can use as a phishing template.
        var body =
            $"""
             {greeting}

             {summary}

             If you made this change, there is nothing to do. If you did not,
             sign in and review the passkeys on your account, and contact us if
             anything there is unfamiliar.
             """;

        await SendAsync(recipient, subject, body, cancellationToken);
    }

    private async Task SendAsync(
        AccountEmailRecipient recipient,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        var from = EmailAddress.Create(_email.FromAddress, _email.FromName);

        var replyTo = string.IsNullOrWhiteSpace(_email.ReplyToAddress)
            ? null
            : EmailAddress.Create(_email.ReplyToAddress);

        var result = await sender.SendAsync(
            new EmailMessage(from, ToAddress(recipient), subject, body, ToHtml(body), replyTo),
            cancellationToken);

        Ensure(result, subject);
    }

    /// <summary>
    /// Wraps the plain-text body in minimal HTML.
    /// </summary>
    /// <remarks>
    /// Everything interpolated into these messages is either a constant or a
    /// URL this application built, but the display name is not — it is whatever
    /// the account holder typed — so it is encoded rather than trusted. An
    /// unencoded display name in an HTML mail body is a stored cross-site
    /// scripting hole that happens to render in a mail client.
    /// </remarks>
    private static string ToHtml(string body)
    {
        var paragraphs = body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(paragraph => $"<p>{System.Net.WebUtility.HtmlEncode(paragraph)}</p>");

        return string.Join('\n', paragraphs);
    }

    private static EmailAddress ToAddress(AccountEmailRecipient recipient) =>
        EmailAddress.Create(recipient.EmailAddress, recipient.DisplayName);

    private static string Describe(TimeSpan lifetime) => lifetime switch
    {
        { TotalHours: >= 2 } => $"{(int)lifetime.TotalHours} hours",
        { TotalHours: >= 1 } => "an hour",
        _ => $"{(int)lifetime.TotalMinutes} minutes",
    };

    private static void Ensure(EmailDeliveryResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        // The reason is provider text and can name the recipient, so it reaches
        // the log through the exception and never reaches the caller: the
        // endpoints above turn this into a bare 500.
        throw new InvalidOperationException(
            $"Account email '{operation}' was not delivered: " +
            $"{result.Failure!.Kind} — {result.Failure.Reason}");
    }
}
