namespace Sw5e.Domain.Moderation;

/// <summary>
/// The spellings of <see cref="FlagReason"/>, <see cref="FlagStatus"/> and
/// <see cref="FlagTargetKind"/> that leave this process.
/// </summary>
/// <remarks>
/// <para>
/// One table, used by both the JSON contract and the database column. The
/// obvious alternative — <c>HasConversion&lt;string&gt;()</c> for storage and a
/// separate map for the wire, which is what the content schema does — is fine
/// where nothing outside the process reads the column, and is a trap here: it
/// makes the C# member name a published identifier in two systems at once, so
/// renaming <c>TextError</c> rewrites every stored row's meaning and every
/// client's switch statement, and neither the compiler nor a test says so.
/// </para>
/// <para>
/// The strings are therefore written down deliberately, in the form the site's
/// URLs and content keys already use, and they are what a reviewer sees in
/// <c>psql</c>. Renaming a member is now free; changing a string below is a
/// migration and a client release, which is the correct amount of friction.
/// </para>
/// <para>
/// Parsing is exact and case-sensitive. Accepting <c>Text-Error</c> and
/// <c>TEXT-ERROR</c> would mean two spellings of one reason reaching the store
/// through a column whose uniqueness constraints compare byte for byte, so the
/// duplicate suppression on the flag table would stop working for anybody who
/// shouted.
/// </para>
/// </remarks>
public static class FlagWire
{
    private static readonly (FlagReason Value, string Name)[] Reasons =
    [
        (FlagReason.ImageArtistKnown, "image-artist-known"),
        (FlagReason.ImageAttributionMissing, "image-attribution-missing"),
        (FlagReason.ImageReplacementWanted, "image-replacement-wanted"),
        (FlagReason.ImageRightsComplaint, "image-rights-complaint"),
        (FlagReason.ImageWrongSubject, "image-wrong-subject"),
        (FlagReason.TextError, "text-error"),
        (FlagReason.ContentIncorrect, "content-incorrect"),
        (FlagReason.ContentMissing, "content-missing"),
        (FlagReason.SourceAttribution, "source-attribution"),
        (FlagReason.Other, "other"),
    ];

    private static readonly (FlagStatus Value, string Name)[] Statuses =
    [
        (FlagStatus.Open, "open"),
        (FlagStatus.Accepted, "accepted"),
        (FlagStatus.Declined, "declined"),
        (FlagStatus.Resolved, "resolved"),
    ];

    private static readonly (FlagTargetKind Value, string Name)[] Kinds =
    [
        (FlagTargetKind.Document, "document"),
        (FlagTargetKind.Image, "image"),
    ];

    /// <summary>Longest wire name any of the three tables holds.</summary>
    /// <remarks>
    /// The column widths are derived from this rather than guessed, so a value
    /// added later cannot be silently truncated by a schema written before it
    /// existed. It is computed, not typed, for the same reason.
    /// </remarks>
    public static readonly int MaxNameLength =
        Math.Max(
            Reasons.Max(entry => entry.Name.Length),
            Math.Max(
                Statuses.Max(entry => entry.Name.Length),
                Kinds.Max(entry => entry.Name.Length)));

    public static string NameOf(FlagReason value) => Lookup(Reasons, value);

    public static string NameOf(FlagStatus value) => Lookup(Statuses, value);

    public static string NameOf(FlagTargetKind value) => Lookup(Kinds, value);

    public static bool TryParseReason(string? name, out FlagReason value) =>
        TryParse(Reasons, name, out value);

    public static bool TryParseStatus(string? name, out FlagStatus value) =>
        TryParse(Statuses, name, out value);

    public static bool TryParseTargetKind(string? name, out FlagTargetKind value) =>
        TryParse(Kinds, name, out value);

    /// <summary>Every reason name, in declaration order.</summary>
    public static IReadOnlyList<string> ReasonNames { get; } =
        [.. Reasons.Select(entry => entry.Name)];

    /// <summary>Every status name, in lifecycle order.</summary>
    public static IReadOnlyList<string> StatusNames { get; } =
        [.. Statuses.Select(entry => entry.Name)];

    private static string Lookup<T>((T Value, string Name)[] table, T value)
        where T : struct, Enum
    {
        foreach (var entry in table)
        {
            if (EqualityComparer<T>.Default.Equals(entry.Value, value))
            {
                return entry.Name;
            }
        }

        // Reached only by adding an enum member and not adding it here, which
        // is a programming error rather than bad input. Throwing keeps a value
        // with no published spelling from being written to a column or a
        // response as an empty string.
        throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            $"No wire name is defined for {typeof(T).Name}.{value}.");
    }

    private static bool TryParse<T>(
        (T Value, string Name)[] table,
        string? name,
        out T value)
        where T : struct, Enum
    {
        foreach (var entry in table)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
