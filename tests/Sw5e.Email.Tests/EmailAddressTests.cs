using Shouldly;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the validation that stands between user input and a mail header.
/// </summary>
public sealed class EmailAddressTests
{
    [Fact]
    public void KeepsTheAddressAndDisplayNameItWasGiven()
    {
        var address = EmailAddress.Create("player@example.com", "Jaina Solo");

        address.Address.ShouldBe("player@example.com");
        address.DisplayName.ShouldBe("Jaina Solo");
        address.ToString().ShouldBe("Jaina Solo <player@example.com>");
    }

    [Fact]
    public void TrimsIncidentalWhitespaceAroundAPastedAddress()
    {
        EmailAddress.Create("  player@example.com  ").Address.ShouldBe("player@example.com");
    }

    /// <summary>
    /// A header is <c>Name: value</c> terminated by CRLF. A carriage return
    /// smuggled into a value lets it end its own header and start another, which
    /// is how a registration form becomes a way to add Bcc recipients to
    /// somebody else's password-reset email.
    /// </summary>
    public static TheoryData<string> HeaderInjectionAttempts() =>
    [
        "player@example.com\r\nBcc: attacker@evil.test",
        "player@example.com\nBcc: attacker@evil.test",
        "player@example.com\r",
        "player@example.com\0",

        // U+0085 NEL is a C1 control that some parsers still treat as a
        // line break, which is how a filter looking only for the ASCII pair
        // gets walked past. Written as an escape because a literal one would
        // terminate this source line.
        "player@example.com\u0085Bcc: attacker@evil.test",
    ];

    [Theory]
    [MemberData(nameof(HeaderInjectionAttempts))]
    public void RejectsAnAddressCarryingAControlCharacter(string address)
    {
        EmailAddress.TryCreate(address, null, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Theory]
    [MemberData(nameof(HeaderInjectionAttempts))]
    public void RejectsADisplayNameCarryingAControlCharacter(string displayName)
    {
        EmailAddress.TryCreate("player@example.com", displayName, out _, out var error)
            .ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    /// <summary>
    /// The framework's parser accepts <c>Name &lt;box@example.com&gt;</c>, which
    /// would otherwise let a display name in through the address argument and
    /// straight past the checks that apply to display names.
    /// </summary>
    [Fact]
    public void RejectsADisplayNameSmuggledInThroughTheAddress()
    {
        EmailAddress.TryCreate(
            "\"Jaina\" <player@example.com>", null, out _, out var error).ShouldBeFalse();

        error.ShouldContain("bare mailbox");
    }

    public static TheoryData<string?> MalformedAddresses() =>
    [
        null,
        "",
        "   ",
        "not-an-address",
        "@example.com",
        "player@",
        "player example@example.com",
        "player@@example.com",
    ];

    [Theory]
    [MemberData(nameof(MalformedAddresses))]
    public void RejectsAMalformedAddress(string? address)
    {
        EmailAddress.TryCreate(address, null, out var result, out var error).ShouldBeFalse();
        result.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RejectsAnAddressLongerThanRfc5321Permits()
    {
        var tooLong = new string('a', EmailAddress.MaxAddressLength) + "@example.com";

        EmailAddress.TryCreate(tooLong, null, out _, out var error).ShouldBeFalse();
        error.ShouldContain(EmailAddress.MaxAddressLength.ToString());
    }

    [Fact]
    public void RejectsAnOversizedDisplayName()
    {
        var tooLong = new string('a', EmailAddress.MaxDisplayNameLength + 1);

        EmailAddress.TryCreate("player@example.com", tooLong, out _, out var error).ShouldBeFalse();
        error.ShouldContain(EmailAddress.MaxDisplayNameLength.ToString());
    }

    [Fact]
    public void CreateThrowsWhereTryCreateWouldReportAnError()
    {
        Should.Throw<ArgumentException>(() => EmailAddress.Create("not-an-address"));
    }

    /// <summary>
    /// No mail system treats <c>Player@</c> and <c>player@</c> as different
    /// people, and treating them as different here would let one account hold
    /// two addresses.
    /// </summary>
    [Fact]
    public void ComparesTheMailboxWithoutRegardToCase()
    {
        var lower = EmailAddress.Create("player@example.com");
        var upper = EmailAddress.Create("Player@Example.COM");

        lower.ShouldBe(upper);
        lower.GetHashCode().ShouldBe(upper.GetHashCode());
    }

    [Fact]
    public void TreatsADifferentDisplayNameAsADifferentValue()
    {
        EmailAddress.Create("player@example.com", "Jaina")
            .ShouldNotBe(EmailAddress.Create("player@example.com", "Jacen"));
    }

    [Fact]
    public void TreatsABlankDisplayNameAsNoDisplayName()
    {
        EmailAddress.Create("player@example.com", "   ").DisplayName.ShouldBeNull();
    }
}
