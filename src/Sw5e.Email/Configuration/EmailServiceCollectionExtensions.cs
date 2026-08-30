using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sw5e.Email.Accounts;
using Sw5e.Email.Providers.Capture;
using Sw5e.Email.Providers.MailerSend;
using Sw5e.Email.Providers.Smtp;
using Sw5e.Email.Resilience;

namespace Sw5e.Email.Configuration;

/// <summary>
/// Wires the email subsystem into the host.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place that knows which provider is which, and it is the
/// only file that has to change when one is added. Everything above
/// <see cref="IEmailSender"/> is written once and works against all of them.
/// </para>
/// <para>
/// Validation happens <b>here</b>, during registration, rather than through
/// <c>ValidateOnStart</c> or on first use. It is the earliest and loudest
/// moment available: the host does not merely fail to start, it fails before
/// it is even built, with an exception naming the exact key. The failure this
/// is designed to prevent is the quiet one — no token configured, every send
/// returning a failure nobody reads, discovered by a locked-out user at three
/// in the morning.
/// </para>
/// </remarks>
public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// The sending identity used when nothing at all is configured and a
    /// fallback provider was offered, which is Development only.
    /// </summary>
    /// <remarks>
    /// <c>.localhost</c> is reserved by RFC 6761 and can never resolve, so the
    /// address is unmistakably a placeholder and could not reach a stranger even
    /// if it somehow escaped into a real send.
    /// </remarks>
    public const string DevelopmentFromAddress = "noreply@sw5e.localhost";

    /// <summary>
    /// Registers the email subsystem, defaulting to
    /// <see cref="EmailProvider.Capture"/> in Development.
    /// </summary>
    /// <remarks>
    /// The Development fallback is what lets the application be cloned and run
    /// with no credentials of any kind. Outside Development there is no
    /// fallback, so a deployment missing its configuration stops instead of
    /// pretending.
    /// </remarks>
    /// <exception cref="EmailConfigurationException">
    /// The configuration is missing or invalid. See the message for the key.
    /// </exception>
    public static IServiceCollection AddSw5eEmail(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return services.AddSw5eEmail(
            configuration,
            environment.IsDevelopment() ? EmailProvider.Capture : null);
    }

    /// <summary>
    /// Registers the email subsystem with an explicit fallback.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">
    /// The root configuration. The <c>Email</c> section is read from it.
    /// </param>
    /// <param name="fallbackProvider">
    /// The provider to use when <c>Email:Provider</c> is not set, or null to
    /// make an unset provider a startup failure. Null is correct for anything
    /// deployed.
    /// </param>
    /// <exception cref="EmailConfigurationException">
    /// The configuration is missing or invalid.
    /// </exception>
    public static IServiceCollection AddSw5eEmail(
        this IServiceCollection services,
        IConfiguration configuration,
        EmailProvider? fallbackProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(EmailOptions.SectionName);

        // Bound to a throwaway instance for validation. The container gets its
        // own binding below; binding twice is cheaper than the alternative,
        // which is building a provider mid-registration to read the options
        // back out of it.
        var options = section.Get<EmailOptions>() ?? new EmailOptions();

        var provider = ResolveProvider(options.Provider, fallbackProvider);

        // Nothing at all configured, and a fallback was offered — which only
        // happens in Development. Filling in a sending identity here is what
        // lets the repository be cloned and run with no email configuration
        // whatsoever, and it is safe precisely because the fallback provider
        // delivers nothing. The placeholder is scoped to that case: setting
        // Email:Provider explicitly, even in Development, means real sending is
        // intended and the sending address must be stated.
        if (string.IsNullOrWhiteSpace(options.Provider) &&
            string.IsNullOrWhiteSpace(options.FromAddress))
        {
            // The .localhost TLD is reserved by RFC 6761 and can never resolve,
            // so a placeholder that somehow escaped into a real send would bounce
            // rather than reach a stranger who happens to own the domain.
            options.FromAddress = DevelopmentFromAddress;
        }

        ValidateCommon(options);
        ValidateRetry(options.Retry);

        services.Configure<EmailOptions>(section);
        services.Configure<EmailRetryOptions>(section.GetSection(nameof(EmailOptions.Retry)));

        // The container binds straight from configuration, which does not carry
        // the placeholder applied above, so the same substitution has to be
        // repeated on the bound instance. PostConfigure rather than a second
        // Configure so it runs after the section binding rather than racing it.
        if (string.Equals(options.FromAddress, DevelopmentFromAddress, StringComparison.Ordinal))
        {
            services.PostConfigure<EmailOptions>(
                bound => bound.FromAddress = DevelopmentFromAddress);
        }

        switch (provider)
        {
            case EmailProvider.MailerSend:
                ValidateMailerSend(options.MailerSend);
                AddMailerSend(services, section, options.MailerSend);
                break;

            case EmailProvider.Smtp:
                ValidateSmtp(options.Smtp);
                services.Configure<SmtpOptions>(section.GetSection(nameof(EmailOptions.Smtp)));
                services.AddSingleton<SmtpEmailSender>();
                AddSenderPipeline<SmtpEmailSender>(services);
                break;

            case EmailProvider.Capture:
                services.AddSingleton<CapturingEmailSender>();
                AddSenderPipeline<CapturingEmailSender>(services);
                break;

            default:
                // Unreachable: ResolveProvider only ever yields a defined
                // member. Present so that adding one to the enum without
                // adding it here fails immediately rather than resolving to no
                // registration at all.
                throw new EmailConfigurationException(
                    $"{EmailOptions.SectionName}:{nameof(EmailOptions.Provider)}",
                    $"'{provider}' is not wired up in {nameof(AddSw5eEmail)}.");
        }

        services.AddSingleton<IAccountEmailService, AccountEmailService>();
        services.AddSingleton<IHostedService>(serviceProvider =>
            new EmailStartupAnnouncer(
                provider,
                options,
                serviceProvider.GetRequiredService<ILoggerFactory>()));

        return services;
    }

    /// <summary>
    /// Registers <paramref name="services"/>' <see cref="IEmailSender"/> as the
    /// chosen provider wrapped in the retry decorator.
    /// </summary>
    /// <remarks>
    /// Every provider goes through the same pipeline. That uniformity is the
    /// point: resilience is not something a provider author has to remember to
    /// add, and it cannot drift between providers because there is only one
    /// copy of it.
    /// </remarks>
    private static void AddSenderPipeline<TProvider>(IServiceCollection services)
        where TProvider : class, IEmailSender
    {
        services.AddSingleton<IEmailSender>(serviceProvider => new RetryingEmailSender(
            serviceProvider.GetRequiredService<TProvider>(),
            serviceProvider.GetRequiredService<IOptions<EmailRetryOptions>>(),
            serviceProvider.GetRequiredService<ILogger<RetryingEmailSender>>()));
    }

    private static void AddMailerSend(
        IServiceCollection services,
        IConfiguration section,
        MailerSendOptions mailerSend)
    {
        services.Configure<MailerSendOptions>(
            section.GetSection(nameof(EmailOptions.MailerSend)));

        // A named client rather than a hand-built HttpClient, so handlers are
        // rotated on the factory's schedule. A long-lived HttpClient never
        // notices a DNS change; a new one per send exhausts ephemeral ports
        // under load. The factory is the only option that does neither.
        services.AddHttpClient(MailerSendEmailSender.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(NormaliseBaseAddress(mailerSend.BaseAddress));
            client.Timeout = mailerSend.Timeout;
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));

            // Identifies this application in MailerSend's request logs, which
            // is what their support will ask for first.
            client.DefaultRequestHeaders.UserAgent.Add(new("sw5e-api", "1.0"));
        });

        services.AddSingleton<MailerSendEmailSender>();
        AddSenderPipeline<MailerSendEmailSender>(services);
    }

    /// <summary>
    /// Guarantees the trailing slash that <see cref="Uri"/> composition needs.
    /// </summary>
    /// <remarks>
    /// Without it, resolving the relative path <c>v1/email</c> against
    /// <c>https://api.mailersend.com</c> replaces the last segment rather than
    /// appending to it. Harmless at the root, silently wrong the moment anyone
    /// configures a base address with a path in it — which a stub or a proxy
    /// will.
    /// </remarks>
    private static string NormaliseBaseAddress(string baseAddress) =>
        baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/";

    private static EmailProvider ResolveProvider(string? configured, EmailProvider? fallback)
    {
        var key = $"{EmailOptions.SectionName}:{nameof(EmailOptions.Provider)}";
        var valid = string.Join(", ", Enum.GetNames<EmailProvider>());

        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback ?? throw new EmailConfigurationException(
                key,
                $"no email provider is configured. Set it to one of: {valid}.");
        }

        // An explicit match rather than Enum.TryParse, which also accepts the
        // underlying numbers — "Email__Provider=1" quietly meaning Smtp is not
        // a configuration surface worth having.
        return configured.Trim().ToLowerInvariant() switch
        {
            "mailersend" => EmailProvider.MailerSend,
            "smtp" => EmailProvider.Smtp,
            "capture" => EmailProvider.Capture,
            _ => throw new EmailConfigurationException(
                key,
                $"'{configured}' is not a known email provider. Use one of: {valid}."),
        };
    }

    private static void ValidateCommon(EmailOptions options)
    {
        if (!EmailAddress.TryCreate(options.FromAddress, options.FromName, out _, out var error))
        {
            throw new EmailConfigurationException(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.FromAddress)}",
                error);
        }

        if (!string.IsNullOrWhiteSpace(options.ReplyToAddress) &&
            !EmailAddress.TryCreate(options.ReplyToAddress, null, out _, out var replyError))
        {
            throw new EmailConfigurationException(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.ReplyToAddress)}",
                replyError);
        }

        if (string.IsNullOrWhiteSpace(options.ProductName))
        {
            throw new EmailConfigurationException(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.ProductName)}",
                "a product name is required; it appears in every subject line.");
        }
    }

    private static void ValidateRetry(EmailRetryOptions retry)
    {
        var prefix = $"{EmailOptions.SectionName}:{nameof(EmailOptions.Retry)}";

        if (retry.MaxAttempts < 1)
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(EmailRetryOptions.MaxAttempts)}",
                "at least one attempt is required; zero would mean never sending anything.");
        }

        if (retry.InitialDelay <= TimeSpan.Zero)
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(EmailRetryOptions.InitialDelay)}",
                "the initial backoff delay must be positive.");
        }

        if (retry.MaxDelay < retry.InitialDelay)
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(EmailRetryOptions.MaxDelay)}",
                "the maximum delay must not be shorter than the initial delay.");
        }
    }

    private static void ValidateMailerSend(MailerSendOptions options)
    {
        var prefix = $"{EmailOptions.SectionName}:{nameof(EmailOptions.MailerSend)}";

        if (string.IsNullOrWhiteSpace(options.ApiToken))
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(MailerSendOptions.ApiToken)}",
                "the MailerSend API token is not configured. It is a secret and must come " +
                "from the environment or a secret store; it is never committed.");
        }

        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress) ||
            (baseAddress.Scheme != Uri.UriSchemeHttps && baseAddress.Scheme != Uri.UriSchemeHttp))
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(MailerSendOptions.BaseAddress)}",
                "the base address must be an absolute http or https URL.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(MailerSendOptions.Timeout)}",
                "the request timeout must be positive.");
        }
    }

    private static void ValidateSmtp(SmtpOptions options)
    {
        var prefix = $"{EmailOptions.SectionName}:{nameof(EmailOptions.Smtp)}";

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(SmtpOptions.Host)}",
                "the SMTP relay hostname is not configured.");
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(SmtpOptions.Port)}",
                "the port must be between 1 and 65535.");
        }

        // Rejected rather than attempted, because the failure mode otherwise is
        // a connection that hangs until the timeout with nothing in the log to
        // explain it: SmtpClient begins in cleartext and an implicit-TLS
        // listener will not answer. See SmtpOptions.UseStartTls.
        if (options.Port == SmtpOptions.ImplicitTlsPort)
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(SmtpOptions.Port)}",
                $"port {SmtpOptions.ImplicitTlsPort} is implicit TLS, which this adapter " +
                $"cannot speak. Use the STARTTLS submission port " +
                $"{SmtpOptions.SubmissionPort} instead.");
        }

        var hasUserName = !string.IsNullOrWhiteSpace(options.UserName);
        var hasPassword = !string.IsNullOrWhiteSpace(options.Password);

        // Half a credential is never intentional, and each half fails
        // differently and confusingly: a username with no password authenticates
        // as an empty string, a password with no username is silently dropped.
        if (hasUserName != hasPassword)
        {
            throw new EmailConfigurationException(
                hasUserName
                    ? $"{prefix}:{nameof(SmtpOptions.Password)}"
                    : $"{prefix}:{nameof(SmtpOptions.UserName)}",
                "an SMTP username and password must be configured together, or neither " +
                "if the relay authenticates by IP address.");
        }

        // AUTH LOGIN is base64, not encryption. Sending a password over a
        // cleartext connection to anything but a local development relay puts
        // it on the wire in recoverable form, so this is an error rather than
        // a warning nobody reads.
        if (hasUserName && !options.UseStartTls && !IsLoopback(options.Host))
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(SmtpOptions.UseStartTls)}",
                "STARTTLS cannot be disabled while credentials are configured for a remote " +
                "relay: SMTP authentication would send the password in the clear.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new EmailConfigurationException(
                $"{prefix}:{nameof(SmtpOptions.Timeout)}",
                "the submission timeout must be positive.");
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
