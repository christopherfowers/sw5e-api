namespace Sw5e.Domain.Content;

/// <summary>
/// Fields a content list may be ordered by.
/// </summary>
/// <remarks>
/// An enum rather than a string so that a database implementation maps each
/// member to a fixed column expression. A caller-supplied sort name is parsed
/// into one of these at the edge, or rejected; it is never carried far enough
/// to be interpolated into SQL.
/// </remarks>
public enum ContentSortField
{
    /// <summary>Display name. The default, and the only ordering that reads naturally.</summary>
    Name,

    /// <summary>Slug. Stable and unique, so it is also the tiebreaker for every other ordering.</summary>
    Key,

    /// <summary>Publication the item came from.</summary>
    SourceKey,

    /// <summary>Core versus expanded content.</summary>
    ContentSet,
}

/// <summary>Ordering direction.</summary>
public enum SortDirection
{
    /// <summary>Ascending: A to Z, lowest first.</summary>
    Ascending,

    /// <summary>Descending: Z to A, highest first.</summary>
    Descending,
}

/// <summary>
/// Everything needed to answer one list request, in one value.
/// </summary>
/// <remarks>
/// Filtering, ordering and paging are all parameters here rather than
/// operations a caller performs on a returned collection. That is the whole
/// point of the shape: a database implementation turns this record into a
/// single <c>WHERE ... ORDER BY ... LIMIT ... OFFSET</c>, so the store returns
/// one page of rows. If any of these were the caller's job, the database
/// implementation would have to materialise every row of a type to serve the
/// second page of it.
/// </remarks>
/// <param name="Type">Resolved content type. A registry instance, never a raw route value.</param>
/// <param name="NameContains">Case-insensitive substring filter on the display name. Null means no filter.</param>
/// <param name="SourceKey">Exact-match filter on the publication. Null means no filter.</param>
/// <param name="ContentSet">Exact-match filter on core versus expanded content. Null means no filter.</param>
/// <param name="SortBy">Field to order by.</param>
/// <param name="Direction">Order direction.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page. Already clamped by the caller to the API's maximum.</param>
public sealed record ContentListQuery(
    ContentTypeDefinition Type,
    string? NameContains,
    string? SourceKey,
    string? ContentSet,
    ContentSortField SortBy,
    SortDirection Direction,
    int Page,
    int PageSize);
