namespace Sw5e.Domain.Content;

/// <summary>
/// The read side of the content store.
/// </summary>
/// <remarks>
/// <para>
/// Four operations, one per thing the site does: build its navigation, browse a
/// type, open an item, search everything. There is deliberately no
/// <c>GetAll</c>: every method that can return more than one item takes the
/// filtering, ordering and cutting as parameters, so the store decides how much
/// work to do. A <c>GetAll</c> plus LINQ would read identically against an
/// in-memory index and would fetch an entire table per request against a
/// database.
/// </para>
/// <para>
/// Every method is asynchronous even though the filesystem implementation
/// completes synchronously, because the database implementation will not, and a
/// synchronous contract cannot be widened later without changing every caller.
/// </para>
/// <para>
/// Implementations are expected to be safe for concurrent use, and are
/// registered as singletons.
/// </para>
/// </remarks>
public interface IContentRepository
{
    /// <summary>
    /// The content type registry with live item counts, which is what the
    /// frontend builds its navigation from.
    /// </summary>
    /// <remarks>
    /// Counts come from the store rather than from a caller looping over
    /// <c>List</c>, so a database implementation answers with one grouped count
    /// query instead of nine paged reads.
    /// </remarks>
    Task<IReadOnlyList<ContentTypeDescriptor>> GetContentTypesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of a single type, filtered and ordered as the query asks.
    /// </summary>
    /// <returns>
    /// The page, its total, and a version token covering exactly this result.
    /// A page beyond the end is an empty page with the real total, not an error.
    /// </returns>
    Task<PagedResult<ContentSummary>> ListAsync(
        ContentListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One item in full, or null when the type holds no item with that key.
    /// </summary>
    /// <param name="type">A resolved registry entry, never a raw route value.</param>
    /// <param name="key">
    /// The item's slug. Implementations must still treat this as untrusted: it
    /// reaches a path join in the filesystem store.
    /// </param>
    Task<ContentDocument?> GetAsync(
        ContentTypeDefinition type,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-text search across every type, already grouped and ranked.
    /// </summary>
    /// <remarks>
    /// Grouping belongs to the store because the cut is per group: returning a
    /// flat ranked list for the caller to bucket would mean over-fetching by
    /// however many types the top results happen to cluster into.
    /// </remarks>
    Task<ContentSearchResult> SearchAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken = default);
}
