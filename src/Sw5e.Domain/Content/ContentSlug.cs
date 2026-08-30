using System.Text.RegularExpressions;

namespace Sw5e.Domain.Content;

/// <summary>
/// The slug format every content key uses, as fixed by the JSON Schemas:
/// lowercase alphanumerics in hyphen-separated groups.
/// </summary>
/// <remarks>
/// This is the first gate a <c>{key}</c> route value passes through. The
/// pattern is anchored and admits no dot, slash, backslash, colon, null or
/// whitespace, so a value that satisfies it cannot escape a directory when
/// joined to a path and cannot name an alternate data stream or a UNC share.
/// The filesystem store re-checks containment afterwards regardless, because a
/// single point of failure guarding a path join is one too few.
/// </remarks>
public static partial class ContentSlug
{
    /// <summary>
    /// Longest slug accepted. Feature keys are built from a granting entry, a
    /// feature name and a level, so they are the longest in the corpus by some
    /// margin; this leaves room for them and still bounds the work an
    /// adversarial request can cause.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>Whether <paramref name="value"/> is a well-formed content slug.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= MaxLength &&
        Pattern().IsMatch(value);

    [GeneratedRegex(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}
