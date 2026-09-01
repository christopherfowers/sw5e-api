namespace Sw5e.Domain.Content;

/// <summary>
/// Bounds on what an authoring request may carry.
/// </summary>
/// <remarks>
/// Constants rather than magic numbers at the endpoint because the same limits
/// are enforced twice — once by the request validator, so an oversized field is
/// refused before it reaches a database round trip, and once by the column
/// width, so a writer that bypasses the endpoint cannot store something the
/// endpoint would have refused. Two enforcement points reading one number
/// cannot disagree about what the limit is.
/// </remarks>
public static class ContentAuthoringLimits
{
    /// <summary>
    /// Longest note an actor may attach to a publication or a revert.
    /// </summary>
    /// <remarks>
    /// Matches the moderation schema's reviewer note. A reason is a sentence
    /// explaining a change, not the change itself, and the same figure was
    /// already judged right for the same kind of text.
    /// </remarks>
    public const int MaxReasonLength = 1000;

    /// <summary>
    /// Largest document, in bytes of UTF-8, an authoring request may carry.
    /// </summary>
    /// <remarks>
    /// The largest document in the corpus today is a rules chapter at about
    /// 466 kB, so the ceiling has to clear that with room for it to grow. One
    /// megabyte does, and still refuses the case this exists for: an
    /// authenticated contributor posting something enormous enough to make the
    /// parse, the validation and the revision snapshot expensive. Refused on
    /// the declared length before the body is read, so the cost of refusing is
    /// not proportional to what was sent.
    /// </remarks>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>Most revisions one history request may return.</summary>
    public const int MaxRevisionPageSize = 100;

    /// <summary>Revisions returned when the caller does not say.</summary>
    public const int DefaultRevisionPageSize = 25;
}

/// <summary>
/// The published spellings of the authoring enumerations.
/// </summary>
/// <remarks>
/// Written out as an explicit table rather than derived from the enum member
/// names. These strings are two contracts at once — the JSON a client parses
/// and the text stored in a column — so renaming a C# member must not be able
/// to silently rewrite either. The mapping is the thing under review; the enum
/// is an implementation detail behind it.
/// </remarks>
public static class ContentAuthoringWire
{
    /// <summary>Wire and column spelling for a revision's action.</summary>
    public static string From(ContentRevisionAction action) => action switch
    {
        ContentRevisionAction.Imported => "imported",
        ContentRevisionAction.Created => "created",
        ContentRevisionAction.Updated => "updated",
        ContentRevisionAction.Reverted => "reverted",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>Reads a stored or received action back.</summary>
    public static ContentRevisionAction ToAction(string value) => value switch
    {
        "imported" => ContentRevisionAction.Imported,
        "created" => ContentRevisionAction.Created,
        "updated" => ContentRevisionAction.Updated,
        "reverted" => ContentRevisionAction.Reverted,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
