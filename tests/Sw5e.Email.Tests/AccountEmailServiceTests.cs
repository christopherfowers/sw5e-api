using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sw5e.Email.Accounts;
using Sw5e.Email.Configuration;
using Sw5e.Email.Providers.Capture;
using Sw5e.Email.Tests.Support;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the templated account messages.
/// </summary>
/// <remarks>
/// The capturing sender is used to get hold of the composed message, and every
/// assertion is about that message's content. None of them is "the sender was
/// called": what matters is that the link reached both parts intact, that
/// untrusted values were encoded for the part they landed in, and that a link
/// that could not safely be put in an anchor was refused outright.
/// </remarks>
public sealed class AccountEmailServiceTests
{
    private const string VerificationUrl = "https://sw5e.test/account/verify?token=abc123&user=7";
    private const string ResetUrl = "https://sw5e.test/account/reset?token=def456&user=7";

    [Fact]
    public async Task VerificationMailCarriesTheLinkInBothParts()
    {
        var (service, capture) = CreateService();

        var result = await service.SendEmailVerificationAsync(
            TestMessages.Recipient, VerificationUrl);

        result.Succeeded.ShouldBeTrue();

        var message = capture.Sent.ShouldHaveSingleItem().Message;

        message.Subject.ShouldBe("Confirm your SW5e email address");

        // The plain-text part carries the URL raw, on its own line, so a client
        // that will not linkify it still leaves something copyable.
        message.PlainTextBody.ShouldContain(VerificationUrl);

        // The HTML part carries it twice: once as the button's target, once as
        // visible text for when the button is stripped or rewritten.
        message.HtmlBody.ShouldContain(
            "href=\"https://sw5e.test/account/verify?token=abc123&amp;user=7\"");
        message.HtmlBody.ShouldContain(
            ">https://sw5e.test/account/verify?token=abc123&amp;user=7<");
    }

    [Fact]
    public async Task PasswordResetMailCarriesTheLinkInBothParts()
    {
        var (service, capture) = CreateService();

        await service.SendPasswordResetAsync(TestMessages.Recipient, ResetUrl);

        var message = capture.Sent.ShouldHaveSingleItem().Message;

        message.Subject.ShouldBe("Reset your SW5e password");
        message.PlainTextBody.ShouldContain(ResetUrl);
        message.HtmlBody.ShouldContain(
            "href=\"https://sw5e.test/account/reset?token=def456&amp;user=7\"");
    }

    /// <summary>
    /// Both messages arrive unsolicited whenever someone types the wrong
    /// address into a form, and the recipient's first question is whether they
    /// have been compromised.
    /// </summary>
    [Fact]
    public async Task BothMessagesSayWhatHappensIfTheyAreIgnored()
    {
        var (service, capture) = CreateService();

        await service.SendEmailVerificationAsync(TestMessages.Recipient, VerificationUrl);
        await service.SendPasswordResetAsync(TestMessages.Recipient, ResetUrl);

        capture.Sent[0].Message.PlainTextBody.ShouldContain("did not create this account");
        capture.Sent[1].Message.PlainTextBody.ShouldContain("Your password has not");
    }

    /// <summary>
    /// A display name is whatever somebody typed into a registration form.
    /// </summary>
    [Fact]
    public async Task EncodesAnUntrustedDisplayNameForTheHtmlPartOnly()
    {
        var (service, capture) = CreateService();
        var hostile = EmailAddress.Create(
            "player@example.com", "<script>alert('pwned')</script>");

        await service.SendEmailVerificationAsync(hostile, VerificationUrl);

        var message = capture.Sent.ShouldHaveSingleItem().Message;

        message.HtmlBody.ShouldNotContain("<script>");
        message.HtmlBody.ShouldContain("&lt;script&gt;alert(&#39;pwned&#39;)&lt;/script&gt;");

        // And the same value unencoded in the text part. Encoding it there too
        // would show the reader "&lt;script&gt;" in their own greeting, which is
        // how a global escape betrays itself.
        message.PlainTextBody.ShouldContain("Hi <script>alert('pwned')</script>,");
    }

    /// <summary>
    /// An unencoded ampersand inside an <c>href</c> is the classic reason a
    /// multi-parameter reset link arrives broken.
    /// </summary>
    [Fact]
    public async Task EncodesTheLinkForTheHtmlAttributeAndLeavesTheTextPartRaw()
    {
        var (service, capture) = CreateService();

        await service.SendPasswordResetAsync(TestMessages.Recipient, ResetUrl);

        var message = capture.Sent.ShouldHaveSingleItem().Message;

        message.HtmlBody.ShouldNotContain("token=def456&user=7");
        message.HtmlBody.ShouldContain("token=def456&amp;user=7");
        message.PlainTextBody.ShouldContain("token=def456&user=7");
        message.PlainTextBody.ShouldNotContain("&amp;");
    }

    /// <summary>
    /// HTML encoding makes a value safe as markup and does nothing about its
    /// scheme: <c>javascript:</c> survives encoding intact and still means
    /// something dangerous in a client that honours it.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert('pwned')")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/account/verify?token=abc")]
    [InlineData("account/verify")]
    [InlineData("")]
    public async Task RefusesALinkThatIsNotAnAbsoluteWebUrl(string url)
    {
        var (service, capture) = CreateService();

        await Should.ThrowAsync<ArgumentException>(
            () => service.SendEmailVerificationAsync(TestMessages.Recipient, url));

        await Should.ThrowAsync<ArgumentException>(
            () => service.SendPasswordResetAsync(TestMessages.Recipient, url));

        capture.Sent.ShouldBeEmpty("nothing should be sent when the link is refused");
    }

    /// <summary>
    /// Permitted so a local development front end can be exercised end to end.
    /// </summary>
    [Fact]
    public async Task AcceptsAPlainHttpLinkForLocalDevelopment()
    {
        var (service, capture) = CreateService();

        await service.SendEmailVerificationAsync(
            TestMessages.Recipient, "http://localhost:5173/verify?token=abc");

        capture.Sent.ShouldHaveSingleItem().Message.PlainTextBody
            .ShouldContain("http://localhost:5173/verify?token=abc");
    }

    public static TheoryData<int, string> LinkLifetimes() => new()
    {
        { 60 * 24 * 2, "This link expires in 2 days and can only be used once." },
        { 60 * 24, "This link expires in 1 day and can only be used once." },
        { 60 * 3, "This link expires in 3 hours and can only be used once." },
        { 60, "This link expires in 1 hour and can only be used once." },
        { 30, "This link expires in 30 minutes and can only be used once." },
        { 1, "This link expires in 1 minute and can only be used once." },
    };

    [Theory]
    [MemberData(nameof(LinkLifetimes))]
    public async Task DescribesTheLinkLifetimeRoundedToAWholeUnit(int minutes, string expected)
    {
        var (service, capture) = CreateService();

        await service.SendPasswordResetAsync(
            TestMessages.Recipient, ResetUrl, TimeSpan.FromMinutes(minutes));

        var message = capture.Sent.ShouldHaveSingleItem().Message;

        message.PlainTextBody.ShouldContain(expected);
        message.HtmlBody.ShouldContain(expected);
    }

    /// <summary>
    /// "Expires in 0 minutes" reads as already expired.
    /// </summary>
    [Fact]
    public async Task RoundsASubMinuteLifetimeUpRatherThanReportingZero()
    {
        var (service, capture) = CreateService();

        await service.SendPasswordResetAsync(
            TestMessages.Recipient, ResetUrl, TimeSpan.FromSeconds(20));

        capture.Sent.ShouldHaveSingleItem().Message.PlainTextBody
            .ShouldContain("expires in 1 minute");
    }

    [Fact]
    public async Task MakesNoClaimAboutTimeWhenNoLifetimeIsGiven()
    {
        var (service, capture) = CreateService();

        await service.SendPasswordResetAsync(TestMessages.Recipient, ResetUrl);

        var body = capture.Sent.ShouldHaveSingleItem().Message.PlainTextBody;

        body.ShouldContain("This link can only be used once.");
        body.ShouldNotContain("expires in");
    }

    [Fact]
    public async Task RefusesToClaimALinkIsAlreadyExpired()
    {
        var (service, _) = CreateService();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => service.SendPasswordResetAsync(
                TestMessages.Recipient, ResetUrl, TimeSpan.Zero));
    }

    [Fact]
    public async Task SendsFromTheConfiguredIdentityWithTheConfiguredReplyTo()
    {
        var (service, capture) = CreateService(options =>
        {
            options.FromAddress = "no-reply@sw5e.test";
            options.FromName = "SW5e Community";
            options.ReplyToAddress = "support@sw5e.test";
        });

        await service.SendEmailVerificationAsync(TestMessages.Recipient, VerificationUrl);

        var message = capture.Sent.ShouldHaveSingleItem().Message;

        message.From.Address.ShouldBe("no-reply@sw5e.test");
        message.From.DisplayName.ShouldBe("SW5e Community");
        message.ReplyTo!.Address.ShouldBe("support@sw5e.test");
        message.To.ShouldBe(TestMessages.Recipient);
    }

    [Fact]
    public async Task SubstitutesTheConfiguredProductNameThroughout()
    {
        var (service, capture) = CreateService(options => options.ProductName = "SW5e Staging");

        await service.SendEmailVerificationAsync(TestMessages.Recipient, VerificationUrl);

        var message = capture.Sent.ShouldHaveSingleItem().Message;

        message.Subject.ShouldBe("Confirm your SW5e Staging email address");
        message.PlainTextBody.ShouldContain("SW5e Staging");
        message.HtmlBody.ShouldContain("SW5e Staging");
    }

    /// <summary>
    /// An empty greeting is what a failed substitution looks like, which is not
    /// a thing to show a reader of a password-reset email.
    /// </summary>
    [Fact]
    public async Task GreetsAnAccountThatHasNoDisplayName()
    {
        var (service, capture) = CreateService();

        await service.SendEmailVerificationAsync(
            EmailAddress.Create("player@example.com"), VerificationUrl);

        capture.Sent.ShouldHaveSingleItem().Message.PlainTextBody.ShouldStartWith("Hi there,");
    }

    /// <summary>
    /// Nothing here fills a template hole with an empty string, so a renamed or
    /// mistyped placeholder cannot reach a reader.
    /// </summary>
    [Fact]
    public async Task LeavesNoUnsubstitutedPlaceholderInEitherPart()
    {
        var (service, capture) = CreateService();

        await service.SendEmailVerificationAsync(
            TestMessages.Recipient, VerificationUrl, TimeSpan.FromHours(24));
        await service.SendPasswordResetAsync(
            TestMessages.Recipient, ResetUrl, TimeSpan.FromHours(1));

        foreach (var captured in capture.Sent)
        {
            captured.Message.Subject.ShouldNotContain("{{");
            captured.Message.PlainTextBody.ShouldNotContain("{{");
            captured.Message.HtmlBody.ShouldNotContain("{{");
        }
    }

    [Fact]
    public async Task ReturnsTheProvidersFailureRatherThanThrowing()
    {
        var failing = new ScriptedEmailSender(
            [EmailDeliveryResult.Permanent("422 the sending domain is not verified")]);

        var service = new AccountEmailService(failing, TestOptions.For(ValidOptions()));

        var result = await service.SendEmailVerificationAsync(
            TestMessages.Recipient, VerificationUrl);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Reason.ShouldBe("422 the sending domain is not verified");
    }

    [Fact]
    public void RefusesToConstructWithAnUnusableSenderAddress()
    {
        var options = ValidOptions();
        options.FromAddress = "not-an-address";

        var exception = Should.Throw<EmailConfigurationException>(() => new AccountEmailService(
            new ScriptedEmailSender([]), TestOptions.For(options)));

        exception.ConfigurationKey.ShouldBe("Email:FromAddress");
    }

    private static EmailOptions ValidOptions() => new()
    {
        Provider = nameof(EmailProvider.Capture),
        FromAddress = "noreply@sw5e.test",
        FromName = "SW5e",
        ProductName = "SW5e",
    };

    private static (IAccountEmailService Service, CapturingEmailSender Capture) CreateService(
        Action<EmailOptions>? configure = null)
    {
        var options = ValidOptions();
        configure?.Invoke(options);

        var capture = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance);

        return (new AccountEmailService(capture, TestOptions.For(options)), capture);
    }
}
