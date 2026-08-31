using System.Text;

namespace Sw5e.Identity.TwoFactor;

/// <summary>
/// Builds the <c>otpauth://</c> URI that an authenticator app reads out of the
/// enrolment QR code.
/// </summary>
/// <remarks>
/// <para>
/// There is no RFC for this. The format every authenticator app implements is
/// the Key Uri Format published alongside Google Authenticator, and the apps
/// that matter — Google Authenticator, Authy, 1Password, Microsoft
/// Authenticator, Aegis — agree on it to the character. Getting it subtly wrong
/// does not produce an error anybody sees: the app happily scans the code and
/// then generates numbers the server will never accept, and the reader
/// concludes that two-factor authentication on this site is broken.
/// </para>
/// <para>
/// The parts that are easy to get wrong, and what this does about each:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>The label.</b> It is a URI <em>path</em>, so it is percent-encoded as a
/// path segment — but the colon separating issuer from account name is
/// structural and must survive encoding. Encoding the whole label with a
/// general-purpose escaper turns that colon into <c>%3A</c>, which several apps
/// then read as part of the account name, producing an entry called
/// "SW5e%3Aperson@example.com". The two halves are therefore encoded
/// separately and joined with a literal colon.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The issuer.</b> Stated twice — once as the label prefix and once as the
/// <c>issuer</c> parameter — because the format says both should be present and
/// should agree. Apps that read only one of the two are common, and apps that
/// warn about a mismatch exist.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The secret.</b> Base32, RFC 4648 alphabet, uppercase, and no padding.
/// Padding characters are the single most common cause of a secret an app
/// refuses to scan: <c>=</c> is legal in the query string but several parsers
/// pass the padding straight into a base32 decoder that rejects it. ASP.NET
/// Core's authenticator key is 160 bits, which encodes to exactly 32 base32
/// characters and so is never padded — this strips anything anyway, because
/// relying on a length coincidence is not a guarantee.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The parameters.</b> <c>algorithm</c>, <c>digits</c> and <c>period</c> are
/// all defaults, and are all stated explicitly. Stating a default is not
/// redundant here: an app whose own default differs would silently generate
/// unusable codes, and the parameter is the only thing that would stop it.
/// </description>
/// </item>
/// </list>
/// </remarks>
public static class AuthenticatorUri
{
    /// <summary>
    /// The name shown above the code in the authenticator app.
    /// </summary>
    /// <remarks>
    /// Short, unambiguous, and the same string in both places it appears. It is
    /// what somebody scrolling a list of thirty accounts uses to find this one.
    /// </remarks>
    public const string Issuer = "SW5e";

    /// <summary>
    /// Builds the URI for one account and one secret.
    /// </summary>
    /// <param name="accountName">
    /// What the app displays beneath the issuer — the account's email address,
    /// so that somebody with two accounts on this site can tell their entries
    /// apart.
    /// </param>
    /// <param name="base32Secret">
    /// The shared secret as ASP.NET Core Identity stores it: base32, no
    /// padding.
    /// </param>
    public static string Build(string accountName, string base32Secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(base32Secret);

        var secret = NormaliseSecret(base32Secret);

        return new StringBuilder("otpauth://totp/")
            .Append(EncodePathSegment(Issuer))
            .Append(':')
            .Append(EncodePathSegment(accountName))
            .Append("?secret=")
            .Append(secret)
            .Append("&issuer=")
            .Append(Uri.EscapeDataString(Issuer))
            .Append("&algorithm=SHA1&digits=")
            .Append(Rfc6238TimeBasedOneTimePassword.Digits)
            .Append("&period=")
            .Append((int)Rfc6238TimeBasedOneTimePassword.Step.TotalSeconds)
            .ToString();
    }

    /// <summary>
    /// The secret in the form a person types into an app that cannot scan.
    /// </summary>
    /// <remarks>
    /// Uppercase, because that is the RFC 4648 alphabet and what every app
    /// displays, and grouped in fours so it can be read off a screen or down a
    /// telephone without losing one's place. Every mainstream app strips the
    /// spaces on entry; none of them object to the case.
    /// </remarks>
    public static string ForManualEntry(string base32Secret)
    {
        var secret = NormaliseSecret(base32Secret);
        var grouped = new StringBuilder(secret.Length + (secret.Length / 4));

        for (var index = 0; index < secret.Length; index += 4)
        {
            if (index > 0)
            {
                grouped.Append(' ');
            }

            grouped.Append(secret.AsSpan(index, Math.Min(4, secret.Length - index)));
        }

        return grouped.ToString();
    }

    private static string NormaliseSecret(string base32Secret) =>
        base32Secret
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("=", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

    /// <summary>
    /// Percent-encodes one half of the label.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.EscapeDataString(string)"/> rather than a URL encoder,
    /// because the label is a path segment and this escapes everything outside
    /// the unreserved set — including the <c>@</c> and any <c>+</c> or space an
    /// address can legitimately contain, and including the colon, which is why
    /// the caller joins the halves rather than passing the whole label through
    /// here.
    /// </remarks>
    private static string EncodePathSegment(string value) => Uri.EscapeDataString(value);
}
