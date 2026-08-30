using Shouldly;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the invariants a message enforces at construction, so that no adapter
/// has to re-check them.
/// </summary>
public sealed class EmailMessageTests
{
    private static EmailMessage Build(
        string subject = "Confirm your SW5e email address",
        string plainText = "text",
        string html = "<p>html</p>") =>
        new(
            from: EmailAddress.Create("noreply@sw5e.test"),
            to: EmailAddress.Create("player@example.com"),
            subject: subject,
            plainTextBody: plainText,
            htmlBody: html);

    [Fact]
    public void KeepsEveryPartItWasGiven()
    {
        var message = new EmailMessage(
            from: EmailAddress.Create("noreply@sw5e.test", "SW5e"),
            to: EmailAddress.Create("player@example.com", "Jaina Solo"),
            subject: "Reset your SW5e password",
            plainTextBody: "text",
            htmlBody: "<p>html</p>",
            replyTo: EmailAddress.Create("support@sw5e.test"));

        message.From.Address.ShouldBe("noreply@sw5e.test");
        message.To.DisplayName.ShouldBe("Jaina Solo");
        message.Subject.ShouldBe("Reset your SW5e password");
        message.PlainTextBody.ShouldBe("text");
        message.HtmlBody.ShouldBe("<p>html</p>");
        message.ReplyTo!.Address.ShouldBe("support@sw5e.test");
    }

    [Fact]
    public void TreatsAnAbsentReplyToAsAbsent()
    {
        Build().ReplyTo.ShouldBeNull();
    }

    /// <summary>
    /// The subject is a header and is the field most likely to contain
    /// something a user typed, so it splits on CR or LF exactly the way an
    /// address does.
    /// </summary>
    [Theory]
    [InlineData("Confirm\r\nBcc: attacker@evil.test")]
    [InlineData("Confirm\nBcc: attacker@evil.test")]
    [InlineData("Confirm\ttabs are control characters too")]
    public void RejectsASubjectCarryingAControlCharacter(string subject)
    {
        Should.Throw<ArgumentException>(() => Build(subject: subject));
    }

    [Fact]
    public void RejectsASubjectLongerThanAHeaderLineAllows()
    {
        var subject = new string('a', EmailMessage.MaxSubjectLength + 1);

        Should.Throw<ArgumentException>(() => Build(subject: subject))
            .Message.ShouldContain(EmailMessage.MaxSubjectLength.ToString());
    }

    [Fact]
    public void AcceptsASubjectExactlyAtTheLimit()
    {
        var subject = new string('a', EmailMessage.MaxSubjectLength);

        Build(subject: subject).Subject.Length.ShouldBe(EmailMessage.MaxSubjectLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsABlankSubject(string subject)
    {
        Should.Throw<ArgumentException>(() => Build(subject: subject));
    }

    /// <summary>
    /// Both parts are required. A text-only client, a screen reader and a spam
    /// filter all read the plain-text alternative, and an HTML-only
    /// transactional email scores badly with the last of those.
    /// </summary>
    [Theory]
    [InlineData("", "<p>html</p>")]
    [InlineData("   ", "<p>html</p>")]
    [InlineData("text", "")]
    [InlineData("text", "   ")]
    public void RejectsAMessageMissingEitherBody(string plainText, string html)
    {
        Should.Throw<ArgumentException>(() => Build(plainText: plainText, html: html));
    }

    [Fact]
    public void RejectsAMessageWithNoSenderOrRecipient()
    {
        Should.Throw<ArgumentNullException>(() => new EmailMessage(
            from: null!,
            to: EmailAddress.Create("player@example.com"),
            subject: "Subject",
            plainTextBody: "text",
            htmlBody: "<p>html</p>"));

        Should.Throw<ArgumentNullException>(() => new EmailMessage(
            from: EmailAddress.Create("noreply@sw5e.test"),
            to: null!,
            subject: "Subject",
            plainTextBody: "text",
            htmlBody: "<p>html</p>"));
    }
}
