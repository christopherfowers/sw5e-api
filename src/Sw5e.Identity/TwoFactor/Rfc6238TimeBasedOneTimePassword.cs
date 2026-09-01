using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Sw5e.Identity.TwoFactor;

/// <summary>
/// RFC 6238 time-based one-time passwords, exactly as an authenticator app
/// computes them.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "works with a real authenticator app" is a property of
/// the algorithm's parameters, and parameters that are left to a framework
/// default are parameters nobody has actually checked. Every one of them is
/// stated here — HMAC-SHA1, a thirty-second step counted from the Unix epoch,
/// six digits, dynamic truncation per RFC 4226 section 5.4, and no
/// per-application modifier mixed into the counter — because those five
/// choices, and only those five, are what Google Authenticator, Authy,
/// 1Password and Microsoft Authenticator all implement.
/// </para>
/// <para>
/// SHA-1 is not a mistake and is not a weakness here. HMAC-SHA1 is unbroken,
/// and the de-facto Key Uri Format that every authenticator app implements
/// treats SHA-1 as the default; apps that accept an <c>algorithm</c> parameter
/// at all frequently ignore it. Choosing SHA-256 here would produce codes that
/// half the world's authenticator apps silently compute wrongly, which is the
/// exact failure this file exists to prevent.
/// </para>
/// <para>
/// The framework ships its own implementation, and it is not used, for one
/// reason: its acceptance window is a hard-coded constant inside an internal
/// type. The window is the single parameter that decides whether somebody whose
/// phone clock has drifted can sign in, so it belongs somewhere it can be
/// stated, reviewed and tested rather than inherited.
/// </para>
/// </remarks>
public static class Rfc6238TimeBasedOneTimePassword
{
    /// <summary>The number of digits in a code.</summary>
    /// <remarks>
    /// Six, because the Key Uri Format's default is six and because an app that
    /// ignores the <c>digits</c> parameter will assume six. Eight would be
    /// stronger and would not work.
    /// </remarks>
    public const int Digits = 6;

    /// <summary>How long one code is current.</summary>
    /// <remarks>
    /// Thirty seconds, the Key Uri Format default, and the value every
    /// mainstream authenticator assumes when <c>period</c> is absent.
    /// </remarks>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The step number for a moment in time.
    /// </summary>
    /// <remarks>
    /// T = floor((unix seconds - T0) / X), with T0 = 0 and X = 30, per RFC 6238
    /// section 4.2. Integer division of a non-negative Unix timestamp floors,
    /// which is what the specification asks for; the platform will not produce
    /// a negative timestamp before the year 1970 comes round again.
    /// </remarks>
    public static long StepNumber(DateTimeOffset moment) =>
        moment.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    /// <summary>Computes the code for one step number.</summary>
    public static string Compute(ReadOnlySpan<byte> key, long stepNumber)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, stepNumber);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        // Dynamic truncation, RFC 4226 section 5.4: the low nibble of the last
        // byte selects a four-byte window, and the top bit of that window is
        // masked off so the result is unambiguously positive on every platform.
        var offset = hash[^1] & 0x0F;

        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000)
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft(Digits, '0');
    }

    /// <summary>
    /// Decides whether a code is one the authenticator holding
    /// <paramref name="key"/> could legitimately be showing right now.
    /// </summary>
    /// <param name="key">The shared secret, already decoded from base32.</param>
    /// <param name="code">The six digits the reader typed.</param>
    /// <param name="stepWindow">
    /// How many steps either side of the current one are accepted. See
    /// <see cref="Sw5eIdentityOptions.AuthenticatorStepWindow"/> for why this is
    /// a parameter rather than a constant, and why its value is one.
    /// </param>
    /// <param name="now">The moment to evaluate against.</param>
    /// <remarks>
    /// <para>
    /// Every candidate is compared in fixed time, and — this is the part that
    /// is easy to get wrong — the loop does not stop early on a match. Breaking
    /// out on the first hit would make the function's running time depend on
    /// <em>which</em> step matched, which leaks how far the caller's clock is
    /// from the server's. That is a small leak, but it is a free one to close.
    /// </para>
    /// </remarks>
    public static bool Verify(
        ReadOnlySpan<byte> key,
        string code,
        int stepWindow,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stepWindow);

        if (code.Length != Digits)
        {
            return false;
        }

        var current = StepNumber(now);
        var matched = false;

        for (var offset = -stepWindow; offset <= stepWindow; offset++)
        {
            var candidate = Compute(key, current + offset);

            // Both operands are the same fixed length by construction, so this
            // compares the digits and nothing else. Accumulated with |= rather
            // than returned, so the number of iterations is the same whether
            // the code was right, wrong, or right at the far edge of the
            // window.
            matched |= CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(candidate),
                System.Text.Encoding.ASCII.GetBytes(code));
        }

        return matched;
    }

    /// <summary>
    /// Decodes a base32 secret of the kind that travels in an
    /// <c>otpauth://</c> URI.
    /// </summary>
    /// <remarks>
    /// RFC 4648 base32, case-insensitive, with padding and the spaces used to
    /// group the secret for manual entry both tolerated. Returns false rather
    /// than throwing on anything else: this parses a value that has been round
    /// tripped through a database and, on the manual-entry path, through
    /// somebody's keyboard.
    /// </remarks>
    public static bool TryDecodeBase32(string? value, out byte[] key)
    {
        key = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new List<byte>(value.Length * 5 / 8);
        var buffer = 0;
        var bitsHeld = 0;

        foreach (var character in value)
        {
            if (character is ' ' or '-' or '=')
            {
                continue;
            }

            var index = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);

            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bitsHeld += 5;

            if (bitsHeld < 8)
            {
                continue;
            }

            bitsHeld -= 8;
            bytes.Add((byte)(buffer >> bitsHeld));
        }

        if (bytes.Count == 0)
        {
            return false;
        }

        key = [.. bytes];
        return true;
    }
}
