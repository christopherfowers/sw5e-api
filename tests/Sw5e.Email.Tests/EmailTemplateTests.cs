using Shouldly;
using Sw5e.Email.Accounts;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the substitution rules directly, rather than only through the messages
/// that use them.
/// </summary>
/// <remarks>
/// Going through <see cref="AccountEmailService"/> proves the current templates
/// are rendered correctly; it does not pin the rules that make any future
/// template safe. These do.
/// </remarks>
public sealed class EmailTemplateTests
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
    {
        ["Name"] = "<b>Jaina</b> & \"friends\"",
        ["Url"] = "https://sw5e.test/verify?token=abc&user=7",
    };

    [Fact]
    public void EncodesEverySubstitutedValueForTheHtmlPart()
    {
        var rendered = EmailTemplate.Render(
            "<p>Hi {{Name}}</p><a href=\"{{Url}}\">go</a>", Values, htmlEncode: true);

        rendered.ShouldBe(
            "<p>Hi &lt;b&gt;Jaina&lt;/b&gt; &amp; &quot;friends&quot;</p>" +
            "<a href=\"https://sw5e.test/verify?token=abc&amp;user=7\">go</a>");
    }

    /// <summary>
    /// Encoding the text part too would show a reader <c>&amp;amp;</c> in their
    /// own greeting, which is how a global escape betrays itself.
    /// </summary>
    [Fact]
    public void LeavesEverySubstitutedValueAloneForThePlainTextPart()
    {
        var rendered = EmailTemplate.Render("Hi {{Name}} - {{Url}}", Values, htmlEncode: false);

        rendered.ShouldBe(
            "Hi <b>Jaina</b> & \"friends\" - https://sw5e.test/verify?token=abc&user=7");
    }

    /// <summary>
    /// The literal parts of the template are trusted and must survive
    /// untouched, or the markup around the values would be encoded into
    /// visible text.
    /// </summary>
    [Fact]
    public void NeverEncodesTheTemplateItself()
    {
        EmailTemplate.Render("<p>&nbsp;</p>", new Dictionary<string, string>(), htmlEncode: true)
            .ShouldBe("<p>&nbsp;</p>");
    }

    /// <summary>
    /// Silent substitution would send a locked-out user "click here to reset
    /// your password:" with nothing after the colon.
    /// </summary>
    [Fact]
    public void ThrowsWhenATemplateReferencesAPlaceholderWithNoValue()
    {
        var exception = Should.Throw<InvalidOperationException>(() => EmailTemplate.Render(
            "Open {{ActionUrl}} to continue.", Values, htmlEncode: false));

        exception.Message.ShouldContain("ActionUrl");
    }

    [Fact]
    public void SubstitutesEveryOccurrenceOfAPlaceholder()
    {
        EmailTemplate.Render("{{Url}} and again {{Url}}", Values, htmlEncode: false)
            .ShouldBe(
                "https://sw5e.test/verify?token=abc&user=7 and " +
                "again https://sw5e.test/verify?token=abc&user=7");
    }

    /// <summary>
    /// So that a stray space inside the braces is a substitution rather than a
    /// literal that ships to a reader.
    /// </summary>
    [Fact]
    public void ToleratesWhitespaceInsideThePlaceholderBraces()
    {
        EmailTemplate.Render("{{ Url }}", Values, htmlEncode: false)
            .ShouldBe("https://sw5e.test/verify?token=abc&user=7");
    }

    /// <summary>
    /// A value is inert once substituted. Otherwise a display name containing
    /// <c>{{ActionUrl}}</c> would rewrite the message it appears in.
    /// </summary>
    [Fact]
    public void DoesNotSubstituteInsideAlreadySubstitutedValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = "{{Url}}",
            ["Url"] = "https://sw5e.test/verify",
        };

        EmailTemplate.Render("Hi {{Name}}", values, htmlEncode: false).ShouldBe("Hi {{Url}}");
    }
}
