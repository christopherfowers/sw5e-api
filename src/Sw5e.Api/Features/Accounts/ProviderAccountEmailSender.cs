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
/// <b>A delivery failure is reported, not thrown.</b> It used to be thrown, and
/// that turned a misconfigured relay into a 500 from <c>register</c> and
/// <c>email/code</c> — the two endpoints whose entire contract is that they
/// answer identically whether or not the address has an account. Both happened
/// to fail identically, because both branches send; the moment somebody dropped
/// the send on the unknown-address branch as a saving, the difference between
/// 500 and 202 would have become a perfect account-existence oracle.
/// </para>
/// <para>
/// The concern behind the throw was right, though: an account flow that
/// swallows an undeliverable message tells the user to check an inbox nothing
/// will ever arrive in. So it is answered somewhere the caller cannot read.
/// Every failure is logged at error with the provider's own reply, and recorded
/// in <see cref="AccountEmailDeliveryMonitor"/>, which the readiness surface
/// reports as degraded. The operator learns everything; the caller learns
/// nothing.
/// </para>
/// <para>
/// Exceptions are still exceptions. A malformed URL, a missing sending
/// identity, an unconfigured public site URL — those are programmer or
/// deployment errors that the code above cannot make right by carrying on, and
/// they still throw. Cancellation still propagates too: a cancelled request is
/// not a failed send and must not be recorded as one.
/// </para>
/// </remarks>
internal sealed class ProviderAccountEmailSender(
    IAccountEmailService accountEmail,
    IEmailSender sender,
    AccountEmailDeliveryMonitor monitor,
    ILogger<ProviderAccountEmailSender> logger,
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

        Record(result, nameof(SendEmailVerificationAsync));
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

        await SendAsync(recipient, subject, body, nameof(SendPasskeyRecoveryAsync), cancellationToken);
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

        await SendAsync(recipient, subject, body, nameof(SendSecurityNoticeAsync), cancellationToken);
    }

    public async Task SendSignInCodeAsync(
        AccountEmailRecipient recipient,
        string code,
        TimeSpan validFor,
        CancellationToken cancellationToken = default)
    {
        // The subject carries no code. Subjects are the one part of a message
        // that shows up on a lock screen, in a notification banner, and in this
        // platform's own delivery logs, and a credential visible without
        // unlocking the phone is a credential the mailbox no longer protects.
        const string subject = "Your SW5e sign-in code";

        var greeting = string.IsNullOrWhiteSpace(recipient.DisplayName)
            ? "Hello,"
            : $"Hello {recipient.DisplayName},";

        var body =
            $"""
             {greeting}

             Your sign-in code is {code}

             Type it into the sign-in page within {Describe(validFor)}. It works
             once, and only for this address.

             If you did not ask to sign in, you can ignore this message and
             nothing will happen. Nobody can use the code without it, and it
             stops working shortly regardless. Nobody from SW5e will ever ask
             you to read this code out or forward it.
             """;

        await SendAsync(recipient, subject, body, nameof(SendSignInCodeAsync), cancellationToken);
    }

    public async Task SendUnknownAddressSignInNoticeAsync(
        string emailAddress,
        CancellationToken cancellationToken = default)
    {
        const string subject = "Sign-in attempt for an SW5e account that does not exist";

        // No display name is available and none is invented: there is no
        // account, so there is nobody to greet by name.
        var body =
            $"""
             Hello,

             Somebody entered this address on the SW5e sign-in page, but there is
             no account here registered to it. No code has been sent and nothing
             has been created.

             If that was you, you can register at {SiteUrl()} and you will be
             asked to confirm this address first.

             If it was not you, there is nothing to do. Somebody typed an
             address; that is all that happened.
             """;

        await SendAsync(
            new AccountEmailRecipient(emailAddress, string.Empty),
            subject,
            body,
            nameof(SendUnknownAddressSignInNoticeAsync),
            cancellationToken);
    }

    /// <summary>
    /// The public site, for the one message that has to point somebody at
    /// registration rather than at a token.
    /// </summary>
    /// <remarks>
    /// Read from configuration for exactly the reason AccountLinks does: a URL
    /// assembled from the incoming request is a URL an attacker with a Host
    /// header gets to choose.
    /// </remarks>
    private string SiteUrl() =>
        string.IsNullOrWhiteSpace(_identity.PublicSiteUrl)
            ? throw new InvalidOperationException(
                "'Identity:PublicSiteUrl' is not configured, so account email cannot be " +
                "addressed. Set it to the public base URL of the site.")
            : _identity.PublicSiteUrl;

    private async Task SendAsync(
        AccountEmailRecipient recipient,
        string subject,
        string body,
        string operation,
        CancellationToken cancellationToken)
    {
        var from = EmailAddress.Create(_email.FromAddress, _email.FromName);

        var replyTo = string.IsNullOrWhiteSpace(_email.ReplyToAddress)
            ? null
            : EmailAddress.Create(_email.ReplyToAddress);

        var result = await sender.SendAsync(
            new EmailMessage(from, ToAddress(recipient), subject, body, ToHtml(body), replyTo),
            cancellationToken);

        Record(result, operation);
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
        { TotalMinutes: >= 2 } => $"{(int)lifetime.TotalMinutes} minutes",
        _ => "a minute",
    };

    /// <summary>
    /// Notes what the provider said, so that a message nobody received is still
    /// a fact somebody holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both kinds are logged at error, including the transient one. By the time
    /// a result reaches here the retry decorator has already spent its
    /// attempts, so "transient" describes the provider's excuse and not the
    /// outcome: the message was not delivered either way, and the reader is
    /// waiting for it either way. The kind is in the line because it tells the
    /// operator whether to look at the relay or at the configuration.
    /// </para>
    /// <para>
    /// The provider's reply is logged and goes no further. It is operator-facing
    /// text that can quote the envelope and so name the recipient, which makes
    /// the application log the widest audience it may have — not the health
    /// surface, which is anonymous, and not the response, which is the caller's.
    /// </para>
    /// </remarks>
    private void Record(EmailDeliveryResult result, string operation)
    {
        if (result.Succeeded)
        {
            monitor.RecordSuccess();
            return;
        }

        var failure = result.Failure!;

        monitor.RecordFailure(failure.Kind);

        logger.LogError(
            "Account email {Operation} was not delivered: {Kind} — {Reason}. The request was " +
            "answered normally, because the response to it must not depend on whether mail " +
            "got out; nothing will arrive in the recipient's inbox.",
            operation,
            failure.Kind,
            failure.Reason);
    }
}
