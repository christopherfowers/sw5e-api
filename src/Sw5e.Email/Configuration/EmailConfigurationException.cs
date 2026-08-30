namespace Sw5e.Email.Configuration;

/// <summary>
/// Thrown when the email configuration is missing or wrong.
/// </summary>
/// <remarks>
/// <para>
/// This is thrown during service registration, which means the host never
/// starts. That is the whole point. The failure mode being designed out is the
/// one where a deployment goes live with no API token, every send quietly
/// returns a failure nobody reads, and the first person to discover it is a
/// locked-out user at three in the morning whose reset email never arrived.
/// A container that will not start is noticed in minutes.
/// </para>
/// <para>
/// The message always names the exact configuration key at fault, in the
/// colon-separated form and — because every deployment of this application
/// configures through environment variables — the double-underscore form as
/// well. It never quotes the offending value: the values in this section are
/// an API token and an SMTP password, and a startup exception is written to
/// logs that are far more widely readable than either secret should be.
/// </para>
/// </remarks>
public sealed class EmailConfigurationException : Exception
{
    /// <summary>
    /// Builds the exception for a single bad or missing key.
    /// </summary>
    /// <param name="configurationKey">
    /// The full key path, colon-separated, for example
    /// <c>Email:MailerSend:ApiToken</c>.
    /// </param>
    /// <param name="problem">
    /// What is wrong with it, phrased for whoever is deploying the thing.
    /// Must not contain the value.
    /// </param>
    public EmailConfigurationException(string configurationKey, string problem)
        : base(BuildMessage(configurationKey, problem))
    {
        ConfigurationKey = configurationKey;
    }

    /// <summary>The configuration key at fault.</summary>
    public string ConfigurationKey { get; }

    private static string BuildMessage(string configurationKey, string problem) =>
        $"Email configuration is invalid at '{configurationKey}': {problem} " +
        $"Set it in configuration, or as the environment variable " +
        $"'{configurationKey.Replace(":", "__", StringComparison.Ordinal)}'. " +
        "The application will not start without it.";
}
