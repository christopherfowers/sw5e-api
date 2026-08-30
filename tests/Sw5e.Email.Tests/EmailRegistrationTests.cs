using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Email.Accounts;
using Sw5e.Email.Configuration;
using Sw5e.Email.Providers.Capture;
using Sw5e.Email.Providers.MailerSend;
using Sw5e.Email.Providers.Smtp;
using Sw5e.Email.Resilience;
using Sw5e.Email.Tests.Support;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the configuration gate and the wiring behind it.
/// </summary>
/// <remarks>
/// Two things are being protected. The first is that misconfiguration stops the
/// application rather than producing one that runs and silently sends nothing —
/// so every case here asserts that registration actually throws, and names the
/// key it throws about. The second is that selecting a provider selects that
/// provider and only that provider.
/// </remarks>
public sealed class EmailRegistrationTests
{
    private const string ApiToken = "mlsn.not-a-real-token-0123456789";

    [Fact]
    public void RefusesToStartWithNoProviderConfiguredOutsideDevelopment()
    {
        var exception = Should.Throw<EmailConfigurationException>(
            () => Register(Settings(provider: null), new TestHostEnvironment("Production")));

        exception.ConfigurationKey.ShouldBe("Email:Provider");
        exception.Message.ShouldContain("MailerSend");
        exception.Message.ShouldContain("Smtp");
        exception.Message.ShouldContain("Capture");
    }

    /// <summary>
    /// So the repository can be cloned and run with no credentials of any kind.
    /// </summary>
    [Fact]
    public void FallsBackToCaptureInDevelopment()
    {
        using var services = Register(
            Settings(provider: null), new TestHostEnvironment("Development"));

        services.GetRequiredService<CapturingEmailSender>().ShouldNotBeNull();
        Unwrap(services).ShouldBeOfType<CapturingEmailSender>();
    }

    /// <summary>
    /// With nothing configured at all — no provider, no sending address — a
    /// Development host still starts. That is the whole point: cloning the
    /// repository and running it must not require an email account.
    /// </summary>
    [Fact]
    public async Task StartsWithNoEmailConfigurationWhatsoeverInDevelopment()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSw5eEmail(configuration, new TestHostEnvironment("Development"));

        await using var provider = services.BuildServiceProvider();

        var capture = provider.GetRequiredService<CapturingEmailSender>();

        await provider.GetRequiredService<IAccountEmailService>()
            .SendEmailVerificationAsync(
                EmailAddress.Create("player@example.com"),
                "http://localhost:5173/verify?token=abc");

        // The placeholder sender is reserved by RFC 6761 and can never resolve,
        // so it cannot reach a stranger even if it escaped into a real send.
        capture.Sent.ShouldHaveSingleItem().Message.From.Address
            .ShouldBe(EmailServiceCollectionExtensions.DevelopmentFromAddress);
    }

    /// <summary>
    /// The placeholder is scoped to "nothing configured". Naming a provider,
    /// even in Development, means real sending is intended and the sending
    /// address has to be stated.
    /// </summary>
    [Fact]
    public void StillRequiresASenderWhenAProviderIsNamedInDevelopment()
    {
        var settings = Settings(provider: "Capture");
        settings.Remove("Email:FromAddress");

        Should.Throw<EmailConfigurationException>(
            () => Register(settings, new TestHostEnvironment("Development")))
            .ConfigurationKey.ShouldBe("Email:FromAddress");
    }

    [Fact]
    public void RefusesAnUnrecognisedProviderNameAndListsTheValidOnes()
    {
        var exception = Should.Throw<EmailConfigurationException>(
            () => Register(Settings(provider: "SendGrid")));

        exception.ConfigurationKey.ShouldBe("Email:Provider");
        exception.Message.ShouldContain("SendGrid");
        exception.Message.ShouldContain("MailerSend, Smtp, Capture");
    }

    /// <summary>
    /// "Email__Provider=1" quietly meaning Smtp is not a configuration surface
    /// worth having.
    /// </summary>
    [Fact]
    public void RefusesTheNumericFormOfTheProviderEnum()
    {
        Should.Throw<EmailConfigurationException>(() => Register(Settings(provider: "1")));
    }

    [Theory]
    [InlineData("mailersend")]
    [InlineData("MAILERSEND")]
    [InlineData("  MailerSend  ")]
    public void MatchesTheProviderNameWithoutRegardToCaseOrSurroundingSpace(string provider)
    {
        var settings = Settings(provider: provider);
        settings["Email:MailerSend:ApiToken"] = ApiToken;

        using var services = Register(settings);

        Unwrap(services).ShouldBeOfType<MailerSendEmailSender>();
    }

    /// <summary>
    /// The failure this whole gate exists to prevent: a deployment with no
    /// token that starts happily and drops every password-reset email.
    /// </summary>
    [Fact]
    public void RefusesMailerSendWithNoApiTokenAndNamesTheEnvironmentVariable()
    {
        var exception = Should.Throw<EmailConfigurationException>(
            () => Register(Settings(provider: "MailerSend")));

        exception.ConfigurationKey.ShouldBe("Email:MailerSend:ApiToken");
        exception.Message.ShouldContain("Email__MailerSend__ApiToken");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://api.mailersend.com/")]
    [InlineData("/v1")]
    public void RefusesAMailerSendBaseAddressThatIsNotAWebUrl(string baseAddress)
    {
        var settings = Settings(provider: "MailerSend");
        settings["Email:MailerSend:ApiToken"] = ApiToken;
        settings["Email:MailerSend:BaseAddress"] = baseAddress;

        Should.Throw<EmailConfigurationException>(() => Register(settings))
            .ConfigurationKey.ShouldBe("Email:MailerSend:BaseAddress");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    public void RefusesAnUnusableSenderAddress(string from)
    {
        var settings = Settings(provider: "Capture");
        settings["Email:FromAddress"] = from;

        Should.Throw<EmailConfigurationException>(() => Register(settings))
            .ConfigurationKey.ShouldBe("Email:FromAddress");
    }

    [Fact]
    public void RefusesAnUnusableReplyToAddress()
    {
        var settings = Settings(provider: "Capture");
        settings["Email:ReplyToAddress"] = "support at sw5e.test";

        Should.Throw<EmailConfigurationException>(() => Register(settings))
            .ConfigurationKey.ShouldBe("Email:ReplyToAddress");
    }

    [Fact]
    public void RefusesSmtpWithNoHost()
    {
        Should.Throw<EmailConfigurationException>(() => Register(Settings(provider: "Smtp")))
            .ConfigurationKey.ShouldBe("Email:Smtp:Host");
    }

    /// <summary>
    /// Rejected at startup rather than producing a connection that hangs until
    /// the timeout with nothing in the log to explain it.
    /// </summary>
    [Fact]
    public void RefusesTheImplicitTlsPortAndPointsAtTheSubmissionPort()
    {
        var settings = SmtpSettings();
        settings["Email:Smtp:Port"] = SmtpOptions.ImplicitTlsPort.ToString();

        var exception = Should.Throw<EmailConfigurationException>(() => Register(settings));

        exception.ConfigurationKey.ShouldBe("Email:Smtp:Port");
        exception.Message.ShouldContain(SmtpOptions.SubmissionPort.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void RefusesAPortOutsideTheValidRange(string port)
    {
        var settings = SmtpSettings();
        settings["Email:Smtp:Port"] = port;

        Should.Throw<EmailConfigurationException>(() => Register(settings))
            .ConfigurationKey.ShouldBe("Email:Smtp:Port");
    }

    /// <summary>
    /// A username with no password authenticates as an empty string; a password
    /// with no username is silently dropped. Neither is ever intentional.
    /// </summary>
    [Theory]
    [InlineData("submission-user", null, "Email:Smtp:Password")]
    [InlineData(null, "not-a-real-password", "Email:Smtp:UserName")]
    public void RefusesHalfAnSmtpCredential(string? userName, string? password, string expectedKey)
    {
        var settings = SmtpSettings();

        if (userName is not null)
        {
            settings["Email:Smtp:UserName"] = userName;
        }

        if (password is not null)
        {
            settings["Email:Smtp:Password"] = password;
        }

        Should.Throw<EmailConfigurationException>(() => Register(settings))
            .ConfigurationKey.ShouldBe(expectedKey);
    }

    /// <summary>
    /// AUTH LOGIN is base64, not encryption.
    /// </summary>
    [Fact]
    public void RefusesToSendCredentialsInTheClearToARemoteRelay()
    {
        var settings = SmtpSettings();
        settings["Email:Smtp:Host"] = "smtp.mailersend.net";
        settings["Email:Smtp:UserName"] = "submission-user";
        settings["Email:Smtp:Password"] = "not-a-real-password";
        settings["Email:Smtp:UseStartTls"] = "false";

        Should.Throw<EmailConfigurationException>(() => Register(settings))
            .ConfigurationKey.ShouldBe("Email:Smtp:UseStartTls");
    }

    /// <summary>
    /// The exception a local development relay depends on: nothing leaves the
    /// machine, so there is no wire to intercept.
    /// </summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void AllowsCleartextCredentialsToALoopbackRelay(string host)
    {
        var settings = SmtpSettings();
        settings["Email:Smtp:Host"] = host;
        settings["Email:Smtp:UserName"] = "developer";
        settings["Email:Smtp:Password"] = "developer";
        settings["Email:Smtp:UseStartTls"] = "false";

        using var services = Register(settings);

        Unwrap(services).ShouldBeOfType<SmtpEmailSender>();
    }

    [Theory]
    [InlineData("Email:Retry:MaxAttempts", "0")]
    [InlineData("Email:Retry:InitialDelay", "00:00:00")]
    [InlineData("Email:Retry:MaxDelay", "00:00:00.100")]
    public void RefusesARetryBudgetThatCannotWork(string key, string value)
    {
        var settings = Settings(provider: "Capture");
        settings[key] = value;

        Should.Throw<EmailConfigurationException>(() => Register(settings))
            .ConfigurationKey.ShouldBe(key);
    }

    /// <summary>
    /// Resilience is applied above the seam, so it must be applied to every
    /// provider identically rather than being something a provider author has
    /// to remember.
    /// </summary>
    [Theory]
    [InlineData("MailerSend", typeof(MailerSendEmailSender))]
    [InlineData("Smtp", typeof(SmtpEmailSender))]
    [InlineData("Capture", typeof(CapturingEmailSender))]
    public void WrapsWhicheverProviderIsSelectedInTheRetryDecorator(string provider, Type expected)
    {
        using var services = Register(SettingsFor(provider));

        var sender = services.GetRequiredService<IEmailSender>();

        sender.ShouldBeOfType<RetryingEmailSender>();
        ((RetryingEmailSender)sender).Inner.ShouldBeOfType(expected);
    }

    /// <summary>
    /// Nothing belonging to an unselected provider should be constructible, or
    /// the "swap" is really a "both are wired up and one of them is used".
    /// </summary>
    [Fact]
    public void RegistersOnlyTheSelectedProvider()
    {
        using var mailerSend = Register(SettingsFor("MailerSend"));

        mailerSend.GetService<SmtpEmailSender>().ShouldBeNull();
        mailerSend.GetService<CapturingEmailSender>().ShouldBeNull();

        using var smtp = Register(SettingsFor("Smtp"));

        smtp.GetService<MailerSendEmailSender>().ShouldBeNull();
        smtp.GetService<CapturingEmailSender>().ShouldBeNull();
    }

    [Fact]
    public void ConfiguresTheMailerSendClientWithTheDocumentedEndpointAndAShortTimeout()
    {
        using var services = Register(SettingsFor("MailerSend"));

        var client = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(MailerSendEmailSender.HttpClientName);

        client.BaseAddress.ShouldBe(new Uri("https://api.mailersend.com/"));

        // Not HttpClient's hundred-second default, which would let a wedged
        // provider hold a registration request open for over a minute and a
        // half — and then the retry decorator would do it three more times.
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
        client.Timeout.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Without the trailing slash, resolving <c>v1/email</c> replaces the last
    /// path segment instead of appending to it.
    /// </summary>
    [Fact]
    public void AppendsAMissingTrailingSlashToTheConfiguredBaseAddress()
    {
        var settings = SettingsFor("MailerSend");
        settings["Email:MailerSend:BaseAddress"] = "https://mail-proxy.sw5e.test/mailersend";

        using var services = Register(settings);

        var client = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(MailerSendEmailSender.HttpClientName);

        new Uri(client.BaseAddress!, "v1/email").ToString()
            .ShouldBe("https://mail-proxy.sw5e.test/mailersend/v1/email");
    }

    [Fact]
    public void RegistersTheAccountServiceAgainstWhicheverProviderIsSelected()
    {
        using var services = Register(SettingsFor("Capture"));

        services.GetRequiredService<IAccountEmailService>().ShouldBeOfType<AccountEmailService>();
    }

    [Fact]
    public void BindsTheConfiguredValuesOntoTheOptionsTheProvidersRead()
    {
        var settings = SettingsFor("MailerSend");
        settings["Email:ProductName"] = "SW5e Staging";
        settings["Email:Retry:MaxAttempts"] = "2";

        using var services = Register(settings);

        var options = services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<EmailOptions>>().Value;

        options.ProductName.ShouldBe("SW5e Staging");
        options.MailerSend.ApiToken.ShouldBe(ApiToken);

        services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<EmailRetryOptions>>().Value
            .MaxAttempts.ShouldBe(2);
    }

    private static Dictionary<string, string?> Settings(string? provider) => new()
    {
        ["Email:Provider"] = provider,
        ["Email:FromAddress"] = "noreply@sw5e.test",
        ["Email:FromName"] = "SW5e",
    };

    private static Dictionary<string, string?> SmtpSettings()
    {
        var settings = Settings("Smtp");
        settings["Email:Smtp:Host"] = "smtp.sw5e.test";

        return settings;
    }

    /// <summary>
    /// A valid configuration for each provider, differing only in the provider
    /// name and the settings that provider requires.
    /// </summary>
    private static Dictionary<string, string?> SettingsFor(string provider)
    {
        var settings = Settings(provider);
        settings["Email:MailerSend:ApiToken"] = ApiToken;
        settings["Email:Smtp:Host"] = "smtp.sw5e.test";

        return settings;
    }

    private static ServiceProvider Register(
        Dictionary<string, string?> settings,
        TestHostEnvironment? environment = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSw5eEmail(configuration, environment ?? new TestHostEnvironment("Production"));

        return services.BuildServiceProvider();
    }

    /// <summary>The provider sitting inside the retry decorator.</summary>
    private static IEmailSender Unwrap(IServiceProvider services) =>
        ((RetryingEmailSender)services.GetRequiredService<IEmailSender>()).Inner;
}
