namespace Sw5e.Api.Features.Content;

/// <summary>
/// Every bound the content endpoints enforce on caller-supplied input.
/// </summary>
/// <remarks>
/// Collected in one place because these are the values that decide how much
/// work a single anonymous request can cause. The endpoints are unauthenticated
/// and read-only, so the only thing standing between a bored visitor and a
/// resource-exhaustion attempt is that each of these is checked and none of
/// them is negotiable by the caller.
/// </remarks>
internal static class ContentRequestLimits
{
    /// <summary>Page served when the caller does not ask for one.</summary>
    public const int DefaultPage = 1;

    /// <summary>Rows per page when the caller does not ask for a size.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>
    /// Largest page a caller may ask for. Rejected rather than clamped: a
    /// client that asked for 5000 rows and silently received 100 will paginate
    /// incorrectly, and finding out why is much harder than reading a 400.
    /// </summary>
    public const int MaxPageSize = 100;

    /// <summary>Longest name filter accepted, in characters.</summary>
    public const int MaxNameFilterLength = 100;

    /// <summary>Shortest search text accepted. One character matches most of the corpus.</summary>
    public const int MinSearchLength = 2;

    /// <summary>
    /// Longest search text accepted. Matching is a linear scan per item, so the
    /// work is bounded by this times the corpus size.
    /// </summary>
    public const int MaxSearchLength = 100;

    /// <summary>Search results per content type when the caller does not ask.</summary>
    public const int DefaultSearchLimit = 5;

    /// <summary>Largest per-type search result count a caller may ask for.</summary>
    public const int MaxSearchLimit = 25;

    /// <summary>Most content types one search request may name explicitly.</summary>
    public const int MaxSearchTypes = 9;

    /// <summary>
    /// How long a content response may be reused. The corpus changes when the
    /// content repository is redeployed, which is on the order of days, so a
    /// few minutes of shared caching costs nothing in freshness and takes the
    /// repeat traffic of the site's most-used feature off the origin.
    /// </summary>
    public const int CacheSeconds = 300;
}
