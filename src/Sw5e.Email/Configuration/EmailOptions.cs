using Sw5e.Email.Providers.MailerSend;
using Sw5e.Email.Providers.Smtp;
using Sw5e.Email.Resilience;

namespace Sw5e.Email.Configuration;

/// <summary>
/// Which sending path is wired up.
/// </summary>
/// <remarks>
/// This enum is the swap. Changing <c>Email:Provider</c> from
/// <c>MailerSend</c> to <c>Smtp</c> changes which adapter satisfies
/// <see cref="IEmailSender"/> and nothing else — not a template, not a call
/// site, not a line of the identity system. Adding a third provider means a new
/// class, a new member here, and a new branch in
/// <see cref="EmailServiceCollectionExtensions"/>.
/// </remarks>
public enum EmailProvider
{
    /// <summary>MailerSend's HTTP API. The intended production provider.</summary>
    MailerSend,

    /// <summary>Any SMTP submission relay, including MailerSend's own.</summary>
    Smtp,

    /// <summary>
    /// Records instead of sending. The default in Development, so the
    /// application runs with no credentials at all.
    /// </summary>
    Capture,
}

/// <summary>
/// The <c>Email</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Bound from whatever configuration providers the host has. In every deployed
/// environment that means environment variables, where a nested key is spelled
/// with a double underscore: <c>Email:MailerSend:ApiToken</c> is
/// <c>Email__MailerSend__ApiToken</c>.
/// </para>
/// <para>
/// <b>Nothing secret has a default and nothing secret is committed.</b> The
/// API token and the SMTP password are the two values that matter, and both are
/// empty here; registration refuses to start without whichever one the selected
/// provider needs. There is deliberately no <c>appsettings.json</c> carrying
/// them, because a placeholder in a committed file is an invitation to replace
/// it with a real value and commit that too.
/// </para>
/// </remarks>
public sealed class EmailOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Email";

    /// <summary>
    /// Which provider to use: <c>MailerSend</c>, <c>Smtp</c> or
    /// <c>Capture</c>, matched without regard to case.
    /// </summary>
    /// <remarks>
    /// Has no default outside Development, where it falls back to
    /// <see cref="EmailProvider.Capture"/>. An unset value anywhere else is a
    /// startup failure: silently picking a provider would mean a production
    /// deployment that looks healthy and sends nothing.
    /// </remarks>
    public string? Provider { get; set; }

    /// <summary>
    /// The address every message is sent from. Required.
    /// </summary>
    /// <remarks>
    /// For MailerSend this must be on a domain verified in the account, or
    /// every send is refused with a 422 that says nothing obvious about why.
    /// </remarks>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>
    /// The display name shown beside <see cref="FromAddress"/>. Optional, and
    /// worth setting: an unnamed sender in an inbox list is the shape of spam.
    /// </summary>
    public string? FromName { get; set; }

    /// <summary>
    /// Where replies should go, when that is not the sending address.
    /// </summary>
    /// <remarks>
    /// Account mail typically goes out from a no-reply mailbox. Pointing this
    /// at something a human reads is what stops a confused reply from
    /// disappearing.
    /// </remarks>
    public string? ReplyToAddress { get; set; }

    /// <summary>
    /// The product name substituted into subjects and bodies. Configurable so
    /// that a staging deployment can announce itself as such rather than
    /// sending mail indistinguishable from production.
    /// </summary>
    public string ProductName { get; set; } = "SW5e";

    /// <summary>MailerSend settings. Used only when that provider is selected.</summary>
    public MailerSendOptions MailerSend { get; set; } = new();

    /// <summary>SMTP settings. Used only when that provider is selected.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>The retry budget, which applies to every provider.</summary>
    public EmailRetryOptions Retry { get; set; } = new();
}
