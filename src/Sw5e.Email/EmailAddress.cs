using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;

namespace Sw5e.Email;

/// <summary>
/// A validated mailbox: an address, and optionally the display name to show
/// beside it.
/// </summary>
/// <remarks>
/// <para>
/// The type exists so that "is this a usable address" is answered once, at the
/// edge, instead of separately by every provider adapter — and so that an
/// adapter can never be handed something a provider will reject or, worse,
/// misinterpret.
/// </para>
/// <para>
/// The specific thing being guarded against is header injection. An SMTP
/// message is a sequence of <c>Name: value</c> headers separated by CRLF, so a
/// carriage return smuggled into an address or a display name lets the value
/// terminate its own header and start another one. That is how a "send me a
/// verification email" form becomes a relay for adding <c>Bcc</c> recipients.
/// Rejecting control characters here means no adapter has to remember to.
/// </para>
/// </remarks>
public sealed class EmailAddress : IEquatable<EmailAddress>
{
    /// <summary>
    /// RFC 5321's upper bound on a full path: 64 octets of local part, an
    /// <c>@</c>, and 255 of domain, minus the angle brackets. Anything longer
    /// is not a mailbox any hop is obliged to accept.
    /// </summary>
    public const int MaxAddressLength = 254;

    /// <summary>
    /// Display names are bounded so an oversized one cannot push a header past
    /// the line limits every relay enforces. Nothing in the product needs a
    /// longer one; real names and product names both fit comfortably.
    /// </summary>
    public const int MaxDisplayNameLength = 128;

    private EmailAddress(string address, string? displayName)
    {
        Address = address;
        DisplayName = displayName;
    }

    /// <summary>The mailbox itself, for example <c>player@example.com</c>.</summary>
    public string Address { get; }

    /// <summary>
    /// The human-readable name shown beside the address, or null when the
    /// address should stand alone.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Builds an address, throwing when the input is not one.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The address is empty, too long, malformed, or contains a control
    /// character; or the display name contains one.
    /// </exception>
    public static EmailAddress Create(string address, string? displayName = null)
    {
        if (!TryCreate(address, displayName, out var result, out var error))
        {
            throw new ArgumentException(error, nameof(address));
        }

        return result;
    }

    /// <summary>
    /// Builds an address, reporting failure rather than throwing.
    /// </summary>
    /// <remarks>
    /// Callers validating user-supplied input want the reason without paying
    /// for an exception, and want to put the reason in a validation response
    /// rather than a log. <paramref name="error"/> therefore describes the
    /// shape of the problem and never quotes the offending value back.
    /// </remarks>
    public static bool TryCreate(
        string? address,
        string? displayName,
        [NotNullWhen(true)] out EmailAddress? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            error = "An email address is required.";
            return false;
        }

        // The control-character check runs against the raw input, before any
        // trimming, and the trim that follows removes spaces only. The order
        // matters more than it looks: Trim() treats CR, LF and tab as
        // whitespace, so trimming first would quietly delete a trailing
        // carriage return and accept the value instead of rejecting it. The
        // result would still be safe, but the rule would no longer be one
        // sentence — and a security control nobody can state in one sentence
        // is one that will eventually be relaxed by accident.
        //
        // For an address this check is currently redundant: MailAddress and the
        // round-trip comparison further down reject the same inputs between
        // them. It stays because that redundancy is incidental — it depends on
        // how strict somebody else's parser happens to be — and a header
        // injection guard should not rest on a property nobody wrote down. For
        // a display name, which no parser sees, it is the only guard there is.
        if (ContainsControlCharacter(address))
        {
            error = "An email address may not contain control characters.";
            return false;
        }

        // A trailing space is the single most common paste artefact and is not
        // a meaningful difference. A space in the middle still fails below.
        address = address.Trim(' ');

        if (address.Length > MaxAddressLength)
        {
            error = $"An email address may not exceed {MaxAddressLength} characters.";
            return false;
        }

        if (displayName is not null)
        {
            if (ContainsControlCharacter(displayName))
            {
                error = "A display name may not contain control characters.";
                return false;
            }

            if (displayName.Length > MaxDisplayNameLength)
            {
                error = $"A display name may not exceed {MaxDisplayNameLength} characters.";
                return false;
            }
        }

        // MailAddress is the framework's own RFC 5322 parser and is what the
        // SMTP adapter will hand the value to anyway, so validating with it
        // guarantees the two agree. A hand-rolled regex would inevitably
        // diverge, and the interesting direction of divergence — this type
        // accepts what MailAddress later rejects — is a crash at send time.
        if (!MailAddress.TryCreate(address, out var parsed))
        {
            error = "That is not a valid email address.";
            return false;
        }

        // MailAddress happily parses "Display Name <box@example.com>", which
        // would let a caller sneak a display name in through the address
        // argument and bypass the length and control-character checks above.
        // Requiring the parse to round-trip to exactly what was passed in
        // closes that off.
        if (!string.Equals(parsed.Address, address, StringComparison.Ordinal))
        {
            error = "Provide the bare mailbox only, without a display name or angle brackets.";
            return false;
        }

        var normalisedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? null
            : displayName.Trim(' ');

        result = new EmailAddress(address, normalisedDisplayName);
        error = null;
        return true;
    }

    /// <summary>
    /// Rejects every C0 and C1 control character, not just CR and LF.
    /// </summary>
    /// <remarks>
    /// CR and LF are the ones that split a header, but a bare NUL truncates a
    /// value in anything that hands the string to unmanaged code, and the C1
    /// range has historically been used to smuggle CR/LF past filters that
    /// only looked for the ASCII pair. The whole class is worthless in a
    /// mailbox, so the whole class goes.
    /// </remarks>
    private static bool ContainsControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The address in <c>Name &lt;box@example.com&gt;</c> form.</summary>
    public override string ToString() =>
        DisplayName is null ? Address : $"{DisplayName} <{Address}>";

    /// <inheritdoc />
    public bool Equals(EmailAddress? other) =>
        other is not null &&
        // Mailbox comparison is case-insensitive in practice: no mail system in
        // use treats Player@ and player@ as different people, and treating them
        // as different here would let the same account hold two addresses.
        string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as EmailAddress);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Address.GetHashCode(StringComparison.OrdinalIgnoreCase),
        DisplayName);
}
