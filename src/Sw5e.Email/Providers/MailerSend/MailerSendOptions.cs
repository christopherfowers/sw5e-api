namespace Sw5e.Email.Providers.MailerSend;

/// <summary>
/// Everything the MailerSend adapter needs, bound from
/// <c>Email:MailerSend</c>.
/// </summary>
public sealed class MailerSendOptions
{
    /// <summary>
    /// MailerSend's documented API host. Overridable so a test or a staging
    /// environment can be pointed at a stub, not because it varies in
    /// production.
    /// </summary>
    public const string DefaultBaseAddress = "https://api.mailersend.com/";

    /// <summary>
    /// The API token, sent as <c>Authorization: Bearer …</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never commit this.</b> It is a bearer credential for sending mail as
    /// a verified domain, which is to say it is a credential for sending
    /// convincing phishing as this project. It has no default here, and
    /// registration refuses to start without it, so there is no configuration
    /// file for anyone to helpfully fill in with a real one.
    /// </para>
    /// <para>
    /// Supply it as the environment variable
    /// <c>Email__MailerSend__ApiToken</c>: a container secret, an App Service
    /// application setting, or a gitignored <c>.env</c> locally. MailerSend
    /// issues tokens per sending domain with granular scopes — the one used
    /// here needs nothing beyond sending.
    /// </para>
    /// </remarks>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>The API root. See <see cref="DefaultBaseAddress"/>.</summary>
    public string BaseAddress { get; set; } = DefaultBaseAddress;

    /// <summary>
    /// How long one HTTP attempt may take before it is abandoned as transient.
    /// </summary>
    /// <remarks>
    /// Ten seconds, against <see cref="HttpClient"/>'s default of one hundred.
    /// The default is indefensible for a call made while a user waits on a
    /// registration form: a provider that has stopped answering would hold the
    /// request open for over a minute and a half, and then the retry decorator
    /// would do it again. A short per-attempt budget plus a bounded number of
    /// attempts is what keeps the worst case survivable.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
