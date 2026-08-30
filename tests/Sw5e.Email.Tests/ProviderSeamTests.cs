using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Email.Accounts;
using Sw5e.Email.Configuration;
using Sw5e.Email.Providers.MailerSend;
using Sw5e.Email.Tests.Support;

namespace Sw5e.Email.Tests;

/// <summary>
/// Proves the provider seam actually swaps.
/// </summary>
/// <remarks>
/// <para>
/// The claim under test is the one the whole design rests on: that changing
/// <c>Email:Provider</c> changes how a message is transmitted and nothing about
/// what is transmitted or who asked for it.
/// </para>
/// <para>
/// So the test runs the <b>same</b> call — the same local function, the same
/// arguments, resolved through the same interface — against two providers that
/// share nothing below <see cref="IEmailSender"/>: JSON over HTTP in one case,
/// a stateful text protocol over a socket in the other. It then recovers the
/// subject and both body parts from each wire format and asserts they are
/// identical.
/// </para>
/// <para>
/// That last assertion is the load-bearing one. Checking that each provider
/// sent "something reasonable" would pass against two implementations that had
/// quietly diverged — a template applied on one path and not the other, an
/// encoding difference, a dropped reply-to. Comparing the two recovered
/// messages to each other means any divergence fails, including one nobody
/// thought to write an assertion for.
/// </para>
/// </remarks>
public sealed class ProviderSeamTests
{
    private const string ResetUrl = "https://sw5e.test/account/reset?token=def456&user=7";

    /// <summary>
    /// The calling code. It appears once and is used for both providers, which
    /// is the point: if the seam leaked, this would have to differ.
    /// </summary>
    private static Task<EmailDeliveryResult> TheAccountFlow(IAccountEmailService email) =>
        email.SendPasswordResetAsync(
            EmailAddress.Create("player@example.com", "Jaina Solo"),
            ResetUrl,
            TimeSpan.FromHours(2));

    [Fact]
    public async Task TheSameCallProducesTheSameMessageThroughEitherProvider()
    {
        var throughMailerSend = await SendThroughMailerSendAsync();
        var throughSmtp = await SendThroughSmtpAsync();

        throughSmtp.Subject.ShouldBe(throughMailerSend.Subject);
        throughSmtp.PlainText.ShouldBe(throughMailerSend.PlainText);
        throughSmtp.Html.ShouldBe(throughMailerSend.Html);

        // And the content is right, not merely consistent: two providers that
        // both sent an empty body would also be identical.
        throughMailerSend.Subject.ShouldBe("Reset your SW5e password");
        throughMailerSend.PlainText.ShouldContain(ResetUrl);
        throughMailerSend.PlainText.ShouldContain("This link expires in 2 hours");
        throughMailerSend.Html.ShouldContain(
            "href=\"https://sw5e.test/account/reset?token=def456&amp;user=7\"");
    }

    [Fact]
    public async Task BothProvidersSendFromTheSameConfiguredIdentityToTheSameRecipient()
    {
        var throughMailerSend = await SendThroughMailerSendAsync();
        var throughSmtp = await SendThroughSmtpAsync();

        foreach (var sent in new[] { throughMailerSend, throughSmtp })
        {
            sent.From.ShouldBe("noreply@sw5e.test");
            sent.FromName.ShouldBe("SW5e");
            sent.To.ShouldBe("player@example.com");
            sent.ReplyTo.ShouldBe("support@sw5e.test");
        }
    }

    /// <summary>
    /// Sends through MailerSend with a stub at the transport boundary, then
    /// reads the message back out of the JSON body.
    /// </summary>
    private static async Task<SentMessage> SendThroughMailerSendAsync()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Accepted);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSw5eEmail(Configuration("MailerSend"), fallbackProvider: null);

        // Replaces only the socket. Base address, timeout, headers, request
        // construction and serialisation are all the registered production
        // configuration.
        services.AddHttpClient(MailerSendEmailSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();

        var result = await TheAccountFlow(provider.GetRequiredService<IAccountEmailService>());
        result.Succeeded.ShouldBeTrue();

        using var document = JsonDocument.Parse(handler.Requests.ShouldHaveSingleItem().Body);
        var root = document.RootElement;

        return new SentMessage(
            Subject: root.GetProperty("subject").GetString()!,
            PlainText: root.GetProperty("text").GetString()!,
            Html: root.GetProperty("html").GetString()!,
            From: root.GetProperty("from").GetProperty("email").GetString()!,
            FromName: root.GetProperty("from").GetProperty("name").GetString(),
            To: root.GetProperty("to")[0].GetProperty("email").GetString()!,
            ReplyTo: root.GetProperty("reply_to").GetProperty("email").GetString());
    }

    /// <summary>
    /// Sends through a real SMTP conversation, then reads the message back out
    /// of the RFC 5322 payload.
    /// </summary>
    private static async Task<SentMessage> SendThroughSmtpAsync()
    {
        await using var relay = new TestSmtpServer();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSw5eEmail(
            Configuration("Smtp", settings =>
            {
                settings["Email:Smtp:Host"] = relay.Host;
                settings["Email:Smtp:Port"] = relay.Port.ToString();
                settings["Email:Smtp:UseStartTls"] = "false";
            }),
            fallbackProvider: null);

        await using var provider = services.BuildServiceProvider();

        var result = await TheAccountFlow(provider.GetRequiredService<IAccountEmailService>());
        result.Succeeded.ShouldBeTrue();

        var parsed = MimeMessage.Parse(relay.Messages.ShouldHaveSingleItem());
        var from = parsed.Header("From");

        return new SentMessage(
            Subject: parsed.Header("Subject"),
            PlainText: parsed.Part("text/plain").Body,
            Html: parsed.Part("text/html").Body,
            From: Mailbox(from),
            FromName: DisplayName(from),
            To: Mailbox(parsed.Header("To")),
            ReplyTo: Mailbox(parsed.Header("Reply-To")));
    }

    /// <summary>
    /// One configuration, differing between the two runs only in the provider
    /// name and the settings that provider requires. Everything a reader sees —
    /// sender identity, reply-to, product name — is shared, which is what makes
    /// the comparison meaningful.
    /// </summary>
    private static IConfiguration Configuration(
        string provider,
        Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Email:Provider"] = provider,
            ["Email:FromAddress"] = "noreply@sw5e.test",
            ["Email:FromName"] = "SW5e",
            ["Email:ReplyToAddress"] = "support@sw5e.test",
            ["Email:ProductName"] = "SW5e",
            ["Email:MailerSend:ApiToken"] = "mlsn.not-a-real-token-0123456789",
        };

        configure?.Invoke(settings);

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>Extracts the mailbox from a possibly named address header.</summary>
    private static string Mailbox(string header)
    {
        var open = header.IndexOf('<', StringComparison.Ordinal);

        return open < 0
            ? header.Trim()
            : header[(open + 1)..header.IndexOf('>', StringComparison.Ordinal)];
    }

    /// <summary>Extracts the display name, unquoted, or null when there is none.</summary>
    private static string? DisplayName(string header)
    {
        var open = header.IndexOf('<', StringComparison.Ordinal);

        return open <= 0 ? null : header[..open].Trim().Trim('"');
    }

    /// <summary>
    /// The parts of a message recovered from a provider's wire format, so that
    /// two entirely different wire formats become comparable.
    /// </summary>
    private sealed record SentMessage(
        string Subject,
        string PlainText,
        string Html,
        string From,
        string? FromName,
        string To,
        string? ReplyTo);
}
