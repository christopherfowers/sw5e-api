namespace Sw5e.Domain.Content;

/// <summary>
/// One page of results together with the total the page was drawn from.
/// </summary>
/// <remarks>
/// <see cref="TotalCount"/> is returned by the store, not counted by the
/// caller, because the caller never holds the full set: a database
/// implementation runs a <c>COUNT(*)</c> over the same predicate as the page
/// query. Returning it here is what lets the UI render "page 3 of 12" without
/// a second round trip.
/// </remarks>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    string Version);
