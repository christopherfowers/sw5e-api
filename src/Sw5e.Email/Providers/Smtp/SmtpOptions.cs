namespace Sw5e.Email.Providers.Smtp;

/// <summary>
/// Everything the SMTP adapter needs, bound from <c>Email:Smtp</c>.
/// </summary>
/// <remarks>
/// Generic on purpose. These are the settings any RFC 5321 submission service
/// asks for, which is what makes the adapter a drop-in for MailerSend's own
/// SMTP endpoint, Amazon SES, Postmark, a corporate relay, or a container
/// running MailHog on a developer's laptop.
/// </remarks>
public sealed class SmtpOptions
{
    /// <summary>
    /// The RFC 6409 message submission port, and the default here.
    /// </summary>
    public const int SubmissionPort = 587;

    /// <summary>
    /// The implicit-TLS submission port, which this adapter cannot use. See
    /// <see cref="UseStartTls"/>.
    /// </summary>
    public const int ImplicitTlsPort = 465;

    /// <summary>The relay's hostname. No default; it must be configured.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// The submission port, defaulting to 587.
    /// </summary>
    /// <remarks>
    /// Not 25. Port 25 is server-to-server relay, is blocked outbound by most
    /// hosting providers and by nearly every residential network, and does not
    /// expect authentication. Submission is 587.
    /// </remarks>
    public int Port { get; set; } = SubmissionPort;

    /// <summary>
    /// The submission username, or empty when the relay authenticates by IP.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// The submission password.
    /// </summary>
    /// <remarks>
    /// <b>Never commit this.</b> Same rules as the MailerSend token: supply it
    /// as <c>Email__Smtp__Password</c> from a secret store, an application
    /// setting, or a gitignored <c>.env</c>. Registration refuses to start if a
    /// username is configured without one.
    /// </remarks>
    public string? Password { get; set; }

    /// <summary>
    /// Whether to upgrade the connection with <c>STARTTLS</c>. On by default,
    /// and there is no supported reason to turn it off outside a loopback
    /// development relay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration treats credentials plus a cleartext connection to a
    /// non-loopback host as a fatal configuration error rather than a warning.
    /// <c>AUTH LOGIN</c> is base64, which is not encryption: the password
    /// crosses the network recoverable by anyone on the path.
    /// </para>
    /// <para>
    /// <b>Known limitation.</b> The adapter is built on
    /// <see cref="System.Net.Mail.SmtpClient"/>, which speaks STARTTLS on a
    /// connection that begins in cleartext and has never supported implicit
    /// TLS — the mode where the TLS handshake happens first, conventionally on
    /// port <see cref="ImplicitTlsPort"/>. Configuring that port is therefore
    /// rejected at startup with an explanation, rather than producing a
    /// connection that hangs until it times out. Every mainstream provider
    /// offers STARTTLS submission on 587; a deployment that genuinely requires
    /// 465 needs a different SMTP client library behind this same adapter,
    /// which is a change confined to this one file.
    /// </para>
    /// </remarks>
    public bool UseStartTls { get; set; } = true;

    /// <summary>
    /// How long one submission attempt may take, connection and handshake
    /// included.
    /// </summary>
    /// <remarks>
    /// Longer than the MailerSend budget because SMTP submission is several
    /// round trips rather than one, and a TLS handshake sits in the middle of
    /// them. Still short enough that a hung relay cannot hold a user's
    /// registration request open indefinitely.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}
