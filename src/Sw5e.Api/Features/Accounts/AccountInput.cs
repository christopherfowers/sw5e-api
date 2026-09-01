using Microsoft.AspNetCore.Http.HttpResults;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// The input checks shared by every anonymous account endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than repeated, and the reason is not tidiness. Registration
/// and email sign-in both take an address, and both promise that a caller
/// cannot tell from the response whether that address has an account. If the
/// two endpoints disagreed by a single character about what counts as a
/// well-formed address, the disagreement would itself be an oracle: an address
/// one accepted and the other rejected would answer 202 from one and 400 from
/// the other, and comparing the two would say something about how the platform
/// stores it.
/// </para>
/// <para>
/// Validation happens before the branch that could leak, never after. A
/// malformed address is not a fact about whether an account exists, so
/// rejecting it loudly costs nothing; from the moment the input is well formed,
/// every path converges on one answer.
/// </para>
/// </remarks>
internal static class AccountInput
{
    /// <summary>
    /// Longest address accepted. Comfortably above the practical maximum for a
    /// deliverable address and well below anything that would make normalising
    /// or indexing it expensive.
    /// </summary>
    public const int MaxEmailLength = 254;

    public const int MaxDisplayNameLength = 64;
    public const int MinDisplayNameLength = 2;

    public static bool TryReadEmail(
        string? value,
        out string emailAddress,
        out ProblemHttpResult? problem)
    {
        emailAddress = value?.Trim() ?? string.Empty;
        problem = null;

        if (emailAddress.Length is 0 or > MaxEmailLength)
        {
            problem = AccountProblems.Invalid(
                $"An email address of 1 to {MaxEmailLength} characters is required.");
            return false;
        }

        // Deliberately shallow. The only address validation that means anything
        // is whether a message sent to it arrives, and that check happens
        // anyway two lines later. A stricter regular expression here would
        // reject deliverable addresses and stop nothing.
        var at = emailAddress.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 ||
            at == emailAddress.Length - 1 ||
            emailAddress.IndexOf('@', at + 1) >= 0 ||
            emailAddress.Any(char.IsWhiteSpace) ||
            emailAddress.Any(char.IsControl))
        {
            problem = AccountProblems.Invalid("That is not a valid email address.");
            return false;
        }

        return true;
    }

    public static bool TryReadDisplayName(
        string? value,
        out string displayName,
        out ProblemHttpResult? problem)
    {
        displayName = value?.Trim() ?? string.Empty;
        problem = null;

        if (displayName.Length is < MinDisplayNameLength or > MaxDisplayNameLength)
        {
            problem = AccountProblems.Invalid(
                $"A display name of {MinDisplayNameLength} to {MaxDisplayNameLength} characters is required.");
            return false;
        }

        // Control characters are stripped at the door rather than at every
        // point of display. A newline or a bidirectional override in a name is
        // never legitimate and is a standard way to make one account's name
        // impersonate another's in a list.
        foreach (var character in displayName)
        {
            if (char.IsControl(character) ||
                char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format)
            {
                problem = AccountProblems.Invalid(
                    "A display name cannot contain control or formatting characters.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Accepts a six-digit code as people actually type it, and rejects
    /// anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used for authenticator codes and emailed codes alike, because readers
    /// paste both the same way: with the space an app puts in the middle, or
    /// with a hyphen a mail client helpfully inserted at a line break.
    /// Stripping separators is a courtesy. The length and digit check that
    /// follows is not — it keeps anything that is not a code out of the
    /// verification path entirely, which matters most on the emailed-code path,
    /// where verification costs a deliberately slow key derivation.
    /// </para>
    /// <para>
    /// Note that a rejection here is not reported to the caller as a distinct
    /// failure by the sign-in endpoints; they answer the same way they answer a
    /// wrong code. Only the enrolment endpoint, which already requires a
    /// session, says specifically that the shape was wrong.
    /// </para>
    /// </remarks>
    public static string? NormaliseDigits(string? code, int digits)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[digits];
        var length = 0;

        foreach (var character in code)
        {
            if (character is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(character) || length == digits)
            {
                return null;
            }

            buffer[length++] = character;
        }

        return length == digits ? new string(buffer) : null;
    }
}
