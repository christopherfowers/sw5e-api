namespace Sw5e.Domain.Content;

/// <summary>
/// Which part of an item the search text was found in. Sent back with every hit
/// so the UI can say why a row is in the list rather than leaving the user to
/// guess.
/// </summary>
public enum SearchMatchField
{
    /// <summary>The display name.</summary>
    Name,

    /// <summary>The slug.</summary>
    Key,

    /// <summary>A short type-specific display field, such as a monster's type or an item's category.</summary>
    Facet,

    /// <summary>The body prose: rules text, lore or flavour.</summary>
    Text,
}

/// <summary>
/// One search request across every content type.
/// </summary>
/// <remarks>
/// <paramref name="MaxPerType"/> is part of the query for the same reason
/// paging is part of <see cref="ContentListQuery"/>: grouping is done by the
/// store, so a database implementation can rank and cut within each type
/// (a windowed query per type, or one query with <c>row_number()</c> partitioned
/// by type) instead of returning every match for the caller to bucket.
/// </remarks>
/// <param name="Text">The search text. Already trimmed and length-capped by the caller.</param>
/// <param name="Types">Restrict to these types, or null for all of them.</param>
/// <param name="MaxPerType">Most hits to return inside any one group.</param>
public sealed record ContentSearchQuery(
    string Text,
    IReadOnlyList<ContentTypeDefinition>? Types,
    int MaxPerType);

/// <summary>
/// One matching item, with the evidence for the match.
/// </summary>
/// <param name="Item">The row projection to render.</param>
/// <param name="MatchedField">Where the text was found.</param>
/// <param name="MatchedFieldName">
/// The specific field name when <see cref="SearchMatchField.Facet"/> was
/// matched, such as "category". Null otherwise.
/// </param>
/// <param name="Snippet">
/// Plain-text context around the match, so the UI can show the phrase in situ.
/// </param>
/// <param name="Score">
/// Relevance, higher is better. Comparable within a response only; it is not a
/// stable quantity across releases or across store implementations.
/// </param>
public sealed record SearchHit(
    ContentSummary Item,
    SearchMatchField MatchedField,
    string? MatchedFieldName,
    string Snippet,
    double Score);

/// <summary>
/// The hits for one content type.
/// </summary>
/// <param name="TotalMatches">
/// How many items of this type matched in total, which is usually more than
/// <see cref="Hits"/> holds. The UI shows it as "showing 5 of 41 powers" and
/// links through to the filtered list.
/// </param>
public sealed record SearchGroup(
    string Type,
    string DisplayName,
    string PluralName,
    string RouteSegment,
    int TotalMatches,
    IReadOnlyList<SearchHit> Hits);

/// <summary>
/// A whole search response: groups in descending order of relevance, each
/// already cut to the requested size.
/// </summary>
public sealed record ContentSearchResult(
    string Query,
    int TotalMatches,
    IReadOnlyList<SearchGroup> Groups,
    string Version);
