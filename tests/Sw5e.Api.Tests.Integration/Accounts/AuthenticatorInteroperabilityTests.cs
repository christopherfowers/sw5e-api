using System.Text;
using Shouldly;
using Sw5e.Identity.TwoFactor;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Whether a real authenticator app can actually sign in to this platform.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because "the two-factor code does not work" is a
/// complaint that no amount of internally consistent code prevents. A server
/// that generates codes with the same routine it validates them with will
/// happily pass its own tests while producing numbers that Google
/// Authenticator, Authy, 1Password and Microsoft Authenticator all compute
/// differently. The tests below therefore never ask the server what a code
/// should be.
/// </para>
/// <para>
/// They anchor to two things outside this repository instead. The first is the
/// set of test vectors published in RFC 6238 Appendix B, which every conformant
/// implementation on earth reproduces — if the server's arithmetic matches
/// those, it matches every authenticator app, and if it ever stops matching
/// them the failure appears here rather than in somebody's inbox. The second is
/// the Key Uri Format that the apps actually parse, which is checked
/// character by character rather than by round-tripping through this codebase's
/// own writer and reader.
/// </para>
/// <para>
/// No database, no HTTP, no fixture. These are properties of an algorithm and a
/// string format, and they are worth failing in a second rather than in the
/// forty it takes to start a container.
/// </para>
/// </remarks>
public sealed class AuthenticatorInteroperabilityTests
{
    /// <summary>
    /// The shared secret from RFC 6238 Appendix B: the ASCII string
    /// "12345678901234567890".
    /// </summary>
    private static readonly byte[] RfcSeed = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>
    /// The published SHA-1 vectors, as (unix time, expected code) pairs.
    /// </summary>
    /// <remarks>
    /// The RFC prints eight-digit codes; this implementation produces six,
    /// which by construction are the low six digits of the same value —
    /// truncation is a modulo, so <c>value % 1e6</c> is the last six digits of
    /// <c>value % 1e8</c>. Taking the tail of the published number rather than
    /// recomputing it keeps this table checkable against the RFC by eye.
    /// </remarks>
    public static TheoryData<long, string> Rfc6238Vectors => new()
    {
        { 59L, "287082" },           // RFC: 94287082
        { 1111111109L, "081804" },   // RFC: 07081804
        { 1111111111L, "050471" },   // RFC: 14050471
        { 1234567890L, "005924" },   // RFC: 89005924
        { 2000000000L, "279037" },   // RFC: 69279037
        { 20000000000L, "353130" },  // RFC: 65353130
    };

    [Theory]
    [MemberData(nameof(Rfc6238Vectors))]
    public void Codes_match_the_published_RFC_6238_vectors(long unixSeconds, string expected)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        var actual = Rfc6238TimeBasedOneTimePassword.Compute(
            RfcSeed,
            Rfc6238TimeBasedOneTimePassword.StepNumber(moment));

        // If this ever fails, no authenticator app on the market can sign in to
        // this platform, and no message the sign-in page could show would
        // explain why to the person holding the phone.
        actual.ShouldBe(expected);
    }

    [Fact]
    public void The_step_number_is_the_unix_time_divided_by_thirty()
    {
        // Not an internal detail: the step number is the counter both sides
        // feed into HMAC, so an off-by-one here is a server that is permanently
        // thirty seconds out of step with every phone.
        Rfc6238TimeBasedOneTimePassword
            .StepNumber(DateTimeOffset.FromUnixTimeSeconds(0)).ShouldBe(0);

        Rfc6238TimeBasedOneTimePassword
            .StepNumber(DateTimeOffset.FromUnixTimeSeconds(29)).ShouldBe(0);

        Rfc6238TimeBasedOneTimePassword
            .StepNumber(DateTimeOffset.FromUnixTimeSeconds(30)).ShouldBe(1);

        Rfc6238TimeBasedOneTimePassword
            .StepNumber(DateTimeOffset.FromUnixTimeSeconds(59)).ShouldBe(1);
    }

    [Fact]
    public void A_code_from_the_current_step_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var code = CodeAt(now, 0);

        Rfc6238TimeBasedOneTimePassword.Verify(RfcSeed, code, stepWindow: 1, now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void A_code_one_step_out_is_accepted(int offset)
    {
        // The whole point of the window. A phone whose clock has drifted a few
        // seconds across a boundary — or a reader who took eight seconds to
        // type — produces exactly this, and rejecting it is the "it does not
        // work" experience that the requirement to have a window exists to
        // prevent.
        var now = DateTimeOffset.UtcNow;

        Rfc6238TimeBasedOneTimePassword
            .Verify(RfcSeed, CodeAt(now, offset), stepWindow: 1, now)
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(-10)]
    [InlineData(10)]
    public void A_code_further_out_than_the_window_is_refused(int offset)
    {
        // The other half, and the half that is easy to lose by widening the
        // window "just to be safe". Every accepted step is another minute in
        // which a code read over somebody's shoulder still works.
        var now = DateTimeOffset.UtcNow;

        Rfc6238TimeBasedOneTimePassword
            .Verify(RfcSeed, CodeAt(now, offset), stepWindow: 1, now)
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    public void A_code_that_is_not_six_digits_is_refused(string code) =>
        Rfc6238TimeBasedOneTimePassword
            .Verify(RfcSeed, code, stepWindow: 1, DateTimeOffset.UtcNow)
            .ShouldBeFalse();

    [Fact]
    public void A_wrong_code_of_the_right_shape_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var correct = CodeAt(now, 0);

        // One digit away, so this cannot pass by accidentally being the right
        // answer for a neighbouring step.
        var wrong = correct[0] == '0'
            ? "1" + correct[1..]
            : "0" + correct[1..];

        Rfc6238TimeBasedOneTimePassword
            .Verify(RfcSeed, wrong, stepWindow: 1, now)
            .ShouldBeFalse();
    }

    [Fact]
    public void A_zero_width_window_accepts_only_the_current_step()
    {
        var now = DateTimeOffset.UtcNow;

        Rfc6238TimeBasedOneTimePassword
            .Verify(RfcSeed, CodeAt(now, 0), stepWindow: 0, now).ShouldBeTrue();

        Rfc6238TimeBasedOneTimePassword
            .Verify(RfcSeed, CodeAt(now, 1), stepWindow: 0, now).ShouldBeFalse();
    }

    [Fact]
    public void The_authenticator_uri_is_exactly_what_the_key_uri_format_asks_for()
    {
        // A secret of exactly the length ASP.NET Core Identity produces: 160
        // bits, which is 32 base32 characters and therefore never padded.
        const string secret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

        var uri = AuthenticatorUri.Build("person@example.com", secret);

        // Asserted as one exact string rather than field by field, because the
        // failure this test exists to catch is a small malformation — an
        // encoded colon, a missing parameter, a lower-cased algorithm name —
        // and a field-by-field check is exactly the shape of test that lets
        // those through.
        uri.ShouldBe(
            "otpauth://totp/SW5e:person%40example.com" +
            "?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP" +
            "&issuer=SW5e&algorithm=SHA1&digits=6&period=30");
    }

    [Fact]
    public void The_label_separator_survives_encoding()
    {
        var uri = AuthenticatorUri.Build("person@example.com", "JBSWY3DPEHPK3PXP");

        // The colon between issuer and account name is structural. Encoding it
        // to %3A produces an entry called "SW5e%3Aperson@example.com" in
        // several apps rather than a grouped one, which is the single most
        // common way a nearly-correct URI goes wrong.
        uri.ShouldContain("/SW5e:person");
        uri.ShouldNotContain("%3A");

        // The at sign, by contrast, is data and must be encoded.
        uri.ShouldContain("person%40example.com");
    }

    [Fact]
    public void The_issuer_in_the_label_matches_the_issuer_parameter()
    {
        var uri = new Uri(AuthenticatorUri.Build("person@example.com", "JBSWY3DPEHPK3PXP"));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

        label.ShouldStartWith(query["issuer"] + ":");
    }

    [Fact]
    public void The_secret_in_the_uri_carries_no_padding_and_no_spaces()
    {
        // Padding is legal in a query string and is rejected by several apps'
        // base32 decoders. A grouped secret — the manual-entry form — pasted
        // into the URI would be worse still.
        var secret = TimeBasedOneTimePassword.SecretFrom(
            AuthenticatorUri.Build("person@example.com", "JBSWY3DPEHPK3PXP"));

        secret.ShouldNotContain("=");
        secret.ShouldNotContain(" ");
        secret.ShouldBe(secret.ToUpperInvariant());
    }

    [Fact]
    public void A_code_computed_from_the_uri_verifies_against_the_server()
    {
        // The end-to-end shape of the interoperability claim, without a
        // database: take the URI the enrolment endpoint hands out, extract the
        // secret exactly as an app scanning the QR code would, compute a code
        // with the independent generator in this suite, and hand it to the
        // server's verifier.
        const string key = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

        var uri = AuthenticatorUri.Build("person@example.com", key);
        var secret = TimeBasedOneTimePassword.SecretFrom(uri);
        var code = TimeBasedOneTimePassword.Generate(secret);

        Rfc6238TimeBasedOneTimePassword.TryDecodeBase32(secret, out var decoded).ShouldBeTrue();

        Rfc6238TimeBasedOneTimePassword
            .Verify(decoded, code, stepWindow: 1, DateTimeOffset.UtcNow)
            .ShouldBeTrue();
    }

    [Fact]
    public void The_manual_entry_form_decodes_to_the_same_secret()
    {
        // Somebody who cannot scan types this string instead, and it has to
        // mean the same thing. Grouped in fours and upper-cased for legibility;
        // every mainstream app strips the spaces on entry.
        const string key = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

        var manual = AuthenticatorUri.ForManualEntry(key);

        manual.ShouldBe("JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP");

        Rfc6238TimeBasedOneTimePassword.TryDecodeBase32(manual, out var fromManual).ShouldBeTrue();
        Rfc6238TimeBasedOneTimePassword.TryDecodeBase32(key, out var fromUri).ShouldBeTrue();

        fromManual.ShouldBe(fromUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base32: 189!")]
    public void A_secret_that_is_not_base32_is_refused(string? value) =>
        Rfc6238TimeBasedOneTimePassword.TryDecodeBase32(value, out _).ShouldBeFalse();

    private static string CodeAt(DateTimeOffset now, int stepOffset) =>
        Rfc6238TimeBasedOneTimePassword.Compute(
            RfcSeed,
            Rfc6238TimeBasedOneTimePassword.StepNumber(now) + stepOffset);
}
