namespace Sw5e.Email.Accounts;

/// <summary>
/// One transactional message, before its placeholders are filled.
/// </summary>
/// <param name="Subject">The subject line template.</param>
/// <param name="PlainText">The <c>text/plain</c> template.</param>
/// <param name="Html">The <c>text/html</c> template.</param>
internal sealed record AccountEmailTemplate(string Subject, string PlainText, string Html);

/// <summary>
/// The account-flow message bodies, as checked-in literals.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not MailerSend's server-side templates. Their template feature
/// is genuinely nicer to edit, but it moves the wording of a password-reset
/// email into a vendor's dashboard: it stops being reviewable, stops being
/// diffable, stops being testable, and stops existing the moment the provider
/// is swapped. Keeping the bodies here is what makes the provider seam real —
/// switching to SMTP changes nothing a reader sees.
/// </para>
/// <para>
/// Both messages are written to the same shape, and the shape is doing work:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>A preheader.</b> The first text in the document is what an inbox
///     list shows beside the subject. Without one, clients scrape whatever
///     comes first — historically "View this email in your browser" — and the
///     preview line is wasted.
///   </description></item>
///   <item><description>
///     <b>The link, again, in full, as text.</b> Corporate mail gateways
///     rewrite anchors, some clients refuse to make them clickable, and users
///     forward these to themselves. A visible URL is the fallback that keeps
///     a locked-out account recoverable.
///   </description></item>
///   <item><description>
///     <b>An "if this wasn't you" line saying what happens if it is ignored.</b>
///     These emails arrive unsolicited whenever someone types the wrong
///     address into a reset form, and the recipient's first question is
///     whether they have been compromised. Answering it costs one sentence and
///     stops the support ticket.
///   </description></item>
///   <item><description>
///     <b>Table-based layout with inline styles.</b> Not nostalgia: Outlook
///     renders through Word's HTML engine, which has no flexbox, no grid and
///     no reliable support for a stylesheet. This is the layout that survives.
///   </description></item>
/// </list>
/// <para>
/// Placeholders are limited to <c>ProductName</c>, <c>Greeting</c>,
/// <c>ActionUrl</c> and <c>ExpiryNote</c>, all filled by
/// <see cref="AccountEmailService"/>. <see cref="EmailTemplate"/> throws on any
/// placeholder it has no value for, so adding one here without adding it there
/// fails a test rather than shipping a hole.
/// </para>
/// </remarks>
internal static class AccountEmailTemplates
{
    /// <summary>Sent when a newly registered address needs proving.</summary>
    public static readonly AccountEmailTemplate EmailVerification = new(
        Subject: "Confirm your {{ProductName}} email address",
        PlainText: PlainTextLayout(
            greetingLine: "Hi {{Greeting}},",
            intro:
                "Someone used this address to create a {{ProductName}} account. " +
                "Confirm the address to finish setting it up.",
            actionLabel: "Confirm your email address:",
            closing:
                "If you did not create this account, ignore this email. " +
                "The address will not be added to anything until the link above is used."),
        Html: HtmlLayout(
            preheader: "Confirm your email address to finish setting up your {{ProductName}} account.",
            heading: "Confirm your email address",
            intro:
                "Someone used this address to create a {{ProductName}} account. " +
                "Confirm the address to finish setting it up.",
            buttonLabel: "Confirm email address",
            closing:
                "If you did not create this account, ignore this email. " +
                "The address will not be added to anything until the button above is used."));

    /// <summary>Sent when someone asks to recover access to an account.</summary>
    public static readonly AccountEmailTemplate PasswordReset = new(
        Subject: "Reset your {{ProductName}} password",
        PlainText: PlainTextLayout(
            greetingLine: "Hi {{Greeting}},",
            intro:
                "We received a request to reset the password on the {{ProductName}} " +
                "account registered to this address.",
            actionLabel: "Choose a new password:",
            closing:
                "If you did not ask for this, ignore this email. Your password has not " +
                "changed, and it cannot be changed without the link above."),
        Html: HtmlLayout(
            preheader: "Use the link inside to choose a new {{ProductName}} password.",
            heading: "Reset your password",
            intro:
                "We received a request to reset the password on the {{ProductName}} " +
                "account registered to this address.",
            buttonLabel: "Choose a new password",
            closing:
                "If you did not ask for this, ignore this email. Your password has not " +
                "changed, and it cannot be changed without the button above."));

    /// <summary>
    /// The shared <c>text/plain</c> shape.
    /// </summary>
    /// <remarks>
    /// Hard-wrapped well inside 78 columns, because a plain-text part is
    /// displayed in a fixed-width pane by clients that will not reflow it, and
    /// because a long line risks the quoted-printable soft breaks that turn a
    /// URL into two useless halves. The URL sits alone on its own line for the
    /// same reason: nothing adjacent for a client's auto-linker to swallow.
    /// </remarks>
    private static string PlainTextLayout(
        string greetingLine,
        string intro,
        string actionLabel,
        string closing) =>
        greetingLine + "\r\n" +
        "\r\n" +
        intro + "\r\n" +
        "\r\n" +
        actionLabel + "\r\n" +
        "\r\n" +
        "{{ActionUrl}}\r\n" +
        "\r\n" +
        "{{ExpiryNote}}\r\n" +
        "\r\n" +
        closing + "\r\n" +
        "\r\n" +
        "-- \r\n" +
        "{{ProductName}}\r\n";

    /// <summary>
    /// The shared <c>text/html</c> shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every argument is a trusted literal from this file, and the assembled
    /// result is a template rather than a finished document — the untrusted
    /// values only arrive later, through <see cref="EmailTemplate.Render"/>,
    /// which encodes them. Passing anything here that did not come from this
    /// file would defeat that.
    /// </para>
    /// <para>
    /// No external stylesheet, no web font, no image. Images are blocked by
    /// default in most clients, and a transactional email that is unreadable
    /// until the reader trusts the sender has the dependency backwards.
    /// </para>
    /// </remarks>
    private static string HtmlLayout(
        string preheader,
        string heading,
        string intro,
        string buttonLabel,
        string closing) =>
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <!-- Tells clients that support it that both palettes are handled, which
               stops the aggressive auto-inversion some of them apply otherwise. -->
          <meta name="color-scheme" content="light dark">
          <title>
        """ + heading + """
        </title>
        </head>
        <body style="margin:0;padding:0;background-color:#f4f4f5;">
          <!-- The preview line. Hidden in the body, harvested by the inbox list. -->
          <div style="display:none;max-height:0;overflow:hidden;opacity:0;">
        """ + preheader + """
        </div>
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f4f4f5;">
            <tr>
              <td align="center" style="padding:32px 16px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;background-color:#ffffff;border-radius:8px;border:1px solid #e4e4e7;">
                  <tr>
                    <td style="padding:32px 32px 8px 32px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
                      <h1 style="margin:0 0 16px 0;font-size:20px;line-height:28px;font-weight:600;color:#18181b;">
        """ + heading + """
        </h1>
                      <p style="margin:0 0 16px 0;font-size:15px;line-height:24px;color:#3f3f46;">Hi {{Greeting}},</p>
                      <p style="margin:0 0 24px 0;font-size:15px;line-height:24px;color:#3f3f46;">
        """ + intro + """
        </p>
                    </td>
                  </tr>
                  <tr>
                    <td align="center" style="padding:0 32px 24px 32px;">
                      <!-- A padded anchor rather than a styled button element: Outlook
                           does not render <button>, and a table cell with a link in it
                           is the one construction every client agrees on. -->
                      <a href="{{ActionUrl}}" style="display:inline-block;padding:12px 24px;background-color:#18181b;color:#ffffff;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:15px;font-weight:600;line-height:20px;text-decoration:none;border-radius:6px;">
        """ + buttonLabel + """
        </a>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:0 32px 32px 32px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
                      <p style="margin:0 0 8px 0;font-size:13px;line-height:20px;color:#71717a;">
                        If the button does not work, copy this link into your browser:
                      </p>
                      <!-- word-break matters: signed links are long and a client that
                           will not break them widens the whole message instead. -->
                      <p style="margin:0 0 24px 0;font-size:13px;line-height:20px;color:#3f3f46;word-break:break-all;">{{ActionUrl}}</p>
                      <p style="margin:0 0 8px 0;font-size:13px;line-height:20px;color:#71717a;">{{ExpiryNote}}</p>
                      <p style="margin:0;font-size:13px;line-height:20px;color:#71717a;">
        """ + closing + """
        </p>
                    </td>
                  </tr>
                </table>
                <p style="max-width:560px;margin:16px auto 0 auto;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:12px;line-height:18px;color:#a1a1aa;">
                  {{ProductName}}
                </p>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
}
