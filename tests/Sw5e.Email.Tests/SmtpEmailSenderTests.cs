using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sw5e.Email.Providers.Smtp;
using Sw5e.Email.Tests.Support;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the SMTP adapter against a real listener on a loopback port.
/// </summary>
/// <remarks>
/// Everything asserted here is read off the wire: the commands the adapter
/// issued, the credentials it presented, and the RFC 5322 message it produced,
/// decoded from its transfer encoding the way a mail client would decode it.
/// </remarks>
public sealed class SmtpEmailSenderTests
{
    [Fact]
    public async Task ProducesAMultipartAlternativeCarryingBothBodies()
    {
        await using var relay = new TestSmtpServer();

        var message = new EmailMessage(
            from: EmailAddress.Create("noreply@sw5e.test", "SW5e"),
            to: EmailAddress.Create("player@example.com", "Jaina Solo"),
            subject: "Reset your SW5e password",
            plainTextBody: "Open https://sw5e.test/reset?token=abc&user=7 to continue.",
            htmlBody: "<p>Open <a href=\"https://sw5e.test/reset?token=abc&amp;user=7\">this link</a>.</p>");

        var result = await CreateSender(relay).SendAsync(message);

        result.Succeeded.ShouldBeTrue();

        var parsed = MimeMessage.Parse(relay.Messages.ShouldHaveSingleItem());

        parsed.Header("Content-Type").ShouldContain("multipart/alternative");
        parsed.Parts.Select(part => part.MediaType)
            .ShouldBe(["text/plain", "text/html"],
                "clients render the last acceptable part, so HTML must come second");

        // Decoded content, not the quoted-printable on the wire: a long signed
        // URL is soft-wrapped mid-string, so a raw substring check would pass
        // or fail on line-length accidents rather than on correctness.
        parsed.Part("text/plain").Body
            .ShouldContain("https://sw5e.test/reset?token=abc&user=7");
        parsed.Part("text/html").Body
            .ShouldContain("href=\"https://sw5e.test/reset?token=abc&amp;user=7\"");

        parsed.Part("text/plain").ContentType.ShouldContain("utf-8");
        parsed.Part("text/html").ContentType.ShouldContain("utf-8");
    }

    [Fact]
    public async Task SetsTheEnvelopeAndTheHeadersFromTheMessage()
    {
        await using var relay = new TestSmtpServer();

        var message = new EmailMessage(
            from: EmailAddress.Create("noreply@sw5e.test", "SW5e"),
            to: EmailAddress.Create("player@example.com", "Jaina Solo"),
            subject: "Confirm your SW5e email address",
            plainTextBody: "text",
            htmlBody: "<p>html</p>",
            replyTo: EmailAddress.Create("support@sw5e.test"));

        await CreateSender(relay).SendAsync(message);

        // The envelope, which is what the relay actually routes on. It is
        // separate from the headers, and a message whose headers are right and
        // whose envelope is wrong goes to the wrong person.
        relay.Commands.ShouldContain(command =>
            command.StartsWith("MAIL FROM:<noreply@sw5e.test>", StringComparison.OrdinalIgnoreCase));
        relay.Commands.ShouldContain(command =>
            command.StartsWith("RCPT TO:<player@example.com>", StringComparison.OrdinalIgnoreCase));

        var parsed = MimeMessage.Parse(relay.Messages.ShouldHaveSingleItem());

        parsed.Header("From").ShouldBe("\"SW5e\" <noreply@sw5e.test>");
        parsed.Header("To").ShouldBe("\"Jaina Solo\" <player@example.com>");
        parsed.Header("Subject").ShouldBe("Confirm your SW5e email address");
        parsed.Header("Reply-To").ShouldBe("support@sw5e.test");
    }

    /// <summary>
    /// These messages carry a bearer token. One recipient is the contract; a
    /// second copy of a password-reset link is a security incident.
    /// </summary>
    [Fact]
    public async Task AddressesExactlyOneRecipient()
    {
        await using var relay = new TestSmtpServer();

        await CreateSender(relay).SendAsync(TestMessages.Simple());

        relay.Commands
            .Count(command => command.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(1);

        var parsed = MimeMessage.Parse(relay.Messages.ShouldHaveSingleItem());

        parsed.Headers.ShouldNotContainKey("Bcc");
        parsed.Headers.ShouldNotContainKey("Cc");
    }

    /// <summary>
    /// The containers this runs in have invariant globalisation, which is
    /// exactly the configuration in which a body left to the ambient encoding
    /// arrives as mojibake.
    /// </summary>
    [Fact]
    public async Task CarriesNonAsciiContentThroughAsUtf8()
    {
        await using var relay = new TestSmtpServer();

        var message = new EmailMessage(
            from: EmailAddress.Create("noreply@sw5e.test"),
            to: EmailAddress.Create("player@example.com"),
            subject: "Confirm your SW5e email address",
            plainTextBody: "Bonjour Aayla Secura — vérifiez votre adresse. ☺",
            htmlBody: "<p>Bonjour Aayla Secura — vérifiez votre adresse. ☺</p>");

        await CreateSender(relay).SendAsync(message);

        var parsed = MimeMessage.Parse(relay.Messages.ShouldHaveSingleItem());

        parsed.Part("text/plain").Body
            .ShouldContain("Bonjour Aayla Secura — vérifiez votre adresse. ☺");
        parsed.Part("text/html").Body
            .ShouldContain("Bonjour Aayla Secura — vérifiez votre adresse. ☺");
    }

    [Fact]
    public async Task PresentsTheConfiguredCredentialsToARelayThatAsksForThem()
    {
        await using var relay = new TestSmtpServer(new TestSmtpServerBehaviour
        {
            AdvertiseAuthLogin = true,
        });

        var sender = CreateSender(relay, options =>
        {
            options.UserName = "submission-user";
            options.Password = "not-a-real-password";
        });

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeTrue();

        // Decoded from the base64 the client actually put on the wire, which is
        // the only way to know the configured values were used rather than
        // silently dropped.
        relay.AuthenticatedUserName.ShouldBe("submission-user");
        relay.AuthenticatedPassword.ShouldBe("not-a-real-password");
    }

    [Fact]
    public async Task DoesNotAuthenticateWhenNoCredentialsAreConfigured()
    {
        await using var relay = new TestSmtpServer(new TestSmtpServerBehaviour
        {
            AdvertiseAuthLogin = true,
        });

        var result = await CreateSender(relay).SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeTrue();
        relay.Commands.ShouldNotContain(command =>
            command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        relay.AuthenticatedUserName.ShouldBeNull();
    }

    /// <summary>
    /// RFC 5321 puts the transient/permanent decision in the first digit of the
    /// reply code, which is why the adapter needs no per-relay knowledge.
    /// </summary>
    public static TheoryData<string, EmailFailureKind> RecipientReplies() => new()
    {
        { "550 5.1.1 <player@example.com>: Recipient address rejected", EmailFailureKind.Permanent },
        { "553 5.1.8 Sender address rejected", EmailFailureKind.Permanent },
        { "451 4.3.0 Temporary system problem, try again later", EmailFailureKind.Transient },
        { "452 4.2.2 Mailbox full", EmailFailureKind.Transient },
    };

    [Theory]
    [MemberData(nameof(RecipientReplies))]
    public async Task ClassifiesTheRelaysReplyCodeByItsFirstDigit(string reply, EmailFailureKind expected)
    {
        await using var relay = new TestSmtpServer(new TestSmtpServerBehaviour
        {
            RcptToReply = reply,
        });

        var result = await CreateSender(relay).SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(expected);
        relay.Messages.ShouldBeEmpty("a rejected recipient means DATA is never reached");
    }

    /// <summary>
    /// A relay that refuses the credentials and then refuses the message.
    /// </summary>
    /// <remarks>
    /// This is the shape a bad SMTP password really takes, and it is worth
    /// knowing why. The framework's client does not fail when an
    /// authentication mechanism is rejected — it moves on to the next
    /// mechanism, and having exhausted them it proceeds unauthenticated rather
    /// than throwing. The error therefore surfaces one step later, when the
    /// relay answers MAIL FROM with 530, and it is that reply the adapter
    /// classifies. Permanent is the right answer either way: a rejected
    /// password does not become correct on the second attempt.
    /// </remarks>
    [Fact]
    public async Task ClassifiesRejectedCredentialsAsPermanent()
    {
        await using var relay = new TestSmtpServer(new TestSmtpServerBehaviour
        {
            AdvertiseAuthLogin = true,
            AuthReply = "535 5.7.8 Error: authentication failed",
            MailFromReply = "530 5.7.0 Authentication Required",
        });

        var sender = CreateSender(relay, options =>
        {
            options.UserName = "submission-user";
            options.Password = "the-wrong-password";
        });

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(EmailFailureKind.Permanent);
        result.Failure.Reason.ShouldContain("530");
        relay.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClassifiesARejectedMessageAtTheDataStage()
    {
        await using var relay = new TestSmtpServer(new TestSmtpServerBehaviour
        {
            DataReply = "554 5.7.1 Message rejected as spam",
        });

        var result = await CreateSender(relay).SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(EmailFailureKind.Permanent);
        result.Failure.Reason.ShouldContain("554");
    }

    /// <summary>
    /// A relay that accepts the connection and then stops talking. Distinct
    /// from one that refuses it, and the case a plain connect check misses.
    /// </summary>
    [Fact]
    public async Task TreatsAnUnresponsiveRelayAsTransient()
    {
        await using var relay = new TestSmtpServer(new TestSmtpServerBehaviour
        {
            GreetingDelay = TimeSpan.FromSeconds(30),
        });

        var sender = CreateSender(relay, options => options.Timeout = TimeSpan.FromMilliseconds(250));

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(EmailFailureKind.Transient);
    }

    [Fact]
    public async Task TreatsARefusedConnectionAsTransient()
    {
        var options = new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = ClosedPort(),
            UseStartTls = false,
            Timeout = TimeSpan.FromSeconds(5),
        };

        var sender = new SmtpEmailSender(
            TestOptions.For(options),
            NullLogger<SmtpEmailSender>.Instance);

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(EmailFailureKind.Transient);
    }

    /// <summary>
    /// SMTP hands back no identifier the client can see, so claiming one would
    /// be inventing it.
    /// </summary>
    [Fact]
    public async Task ReportsNoProviderMessageId()
    {
        await using var relay = new TestSmtpServer();

        var result = await CreateSender(relay).SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeTrue();
        result.ProviderMessageId.ShouldBeNull();
    }

    private static SmtpEmailSender CreateSender(
        TestSmtpServer relay,
        Action<SmtpOptions>? configure = null)
    {
        var options = new SmtpOptions
        {
            Host = relay.Host,
            Port = relay.Port,

            // The fixture speaks cleartext. Configuration refuses this pairing
            // for a remote host; loopback is the exception it allows, and this
            // is loopback.
            UseStartTls = false,
            Timeout = TimeSpan.FromSeconds(15),
        };

        configure?.Invoke(options);

        return new SmtpEmailSender(TestOptions.For(options), NullLogger<SmtpEmailSender>.Instance);
    }

    /// <summary>
    /// Binds an ephemeral port and releases it, so the number is one nothing is
    /// listening on. Racier than a fixed port in principle and far less racy in
    /// practice, where a hard-coded number is exactly what another test or
    /// another CI job is already using.
    /// </summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
