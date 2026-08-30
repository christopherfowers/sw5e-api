using System.Globalization;
using System.Security.Cryptography;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// A standards-conformant RFC 6238 code generator, written independently of the
/// server's.
/// </summary>
/// <remarks>
/// <para>
/// Written out by hand rather than obtained by asking the server's own
/// <c>UserManager</c> to generate one, and the distinction matters. A test that
/// generates a code with the same code that validates it proves only that the
/// implementation agrees with itself — it would pass just as well if the
/// algorithm were wrong in a way that made the codes useless in a real
/// authenticator app.
/// </para>
/// <para>
/// This computes what Google Authenticator, 1Password or Aegis would compute
/// from the <c>otpauth://</c> URI the enrolment endpoint hands out: base32
/// secret, HMAC-SHA1, thirty-second steps, six digits. If the server ever
/// stopped agreeing with that, the tests would fail — which is the correct
/// outcome, because at that point real users could no longer sign in.
/// </para>
/// </remarks>
internal static class TimeBasedOneTimePassword
{
    private const int Digits = 6;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>Computes the current code for a base32-encoded secret.</summary>
    public static string Generate(string base32Secret, long stepOffset = 0)
    {
        var key = DecodeBase32(base32Secret);
        var counter = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)Step.TotalSeconds) + stepOffset;

        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counterBytes, hash);

        // Dynamic truncation, exactly as RFC 4226 section 5.4 describes it.
        var offset = hash[^1] & 0x0F;

        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString(
            CultureInfo.InvariantCulture.NumberFormat).PadLeft(Digits, '0');
    }

    /// <summary>
    /// Reads the secret out of an <c>otpauth://</c> URI, which is the value a
    /// real authenticator app would extract from the QR code.
    /// </summary>
    public static string SecretFrom(string authenticatorUri) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(authenticatorUri).Query)["secret"]
        ?? throw new InvalidOperationException("The otpauth URI carried no secret.");

    private static byte[] DecodeBase32(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new List<byte>();
        var buffer = 0;
        var bitsHeld = 0;

        foreach (var character in value.Replace(" ", string.Empty).TrimEnd('=').ToUpperInvariant())
        {
            var index = Alphabet.IndexOf(character, StringComparison.Ordinal);

            if (index < 0)
            {
                throw new FormatException($"'{character}' is not a base32 character.");
            }

            buffer = (buffer << 5) | index;
            bitsHeld += 5;

            if (bitsHeld >= 8)
            {
                bitsHeld -= 8;
                bytes.Add((byte)(buffer >> bitsHeld));
            }
        }

        return [.. bytes];
    }
}
