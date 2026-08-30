using System.Text.Json;
using Sw5e.Domain.Content;

namespace Sw5e.Api.Features.Content;

/// <summary>The content type registry, which the site builds its navigation from.</summary>
/// <param name="Types">Every type the API serves, in navigation order.</param>
public sealed record ContentTypesResponse(IReadOnlyList<ContentTypeResponse> Types);

/// <summary>One entry in the content type registry.</summary>
/// <param name="Key">Canonical key, and the value to use for the <c>type</c> route parameter.</param>
/// <param name="Name">Singular display label.</param>
/// <param name="PluralName">Plural display label.</param>
/// <param name="RouteSegment">Slug the site uses in its own URLs.</param>
/// <param name="ItemCount">How many items of this type are currently available.</param>
public sealed record ContentTypeResponse(
    string Key,
    string Name,
    string PluralName,
    string RouteSegment,
    int ItemCount);

/// <summary>A row in a content list or a search result.</summary>
/// <param name="Type">Canonical content type key.</param>
/// <param name="Key">Slug identifying the item within its type.</param>
/// <param name="Name">Display name.</param>
/// <param name="SourceKey">Publication the item came from, absent on types that do not record one.</param>
/// <param name="ContentSet">Either <c>core</c> or <c>expanded-content</c>, absent on types that do not record it.</param>
/// <param name="Summary">One-line plain-text description, already truncated for display.</param>
/// <param name="Facets">
/// Type-specific display fields, such as <c>level</c> on a power or
/// <c>challengeRating</c> on a monster. Keys are field paths from the type's
/// JSON Schema; absent fields are omitted rather than sent as null.
/// </param>
public sealed record ContentItemSummaryResponse(
    string Type,
    string Key,
    string Name,
    string? SourceKey,
    string? ContentSet,
    string? Summary,
    IReadOnlyDictionary<string, string> Facets)
{
    internal static ContentItemSummaryResponse From(ContentSummary summary) =>
        new(summary.Type,
            summary.Key,
            summary.Name,
            summary.SourceKey,
            summary.ContentSet,
            summary.Summary,
            summary.Facets);
}

/// <summary>Where a page sits in the full result set.</summary>
/// <param name="Number">1-based page number that was served.</param>
/// <param name="Size">Rows per page.</param>
/// <param name="TotalItems">Rows matching the filters, across all pages.</param>
/// <param name="TotalPages">Pages the result set spans, at this page size.</param>
public sealed record PageInfo(int Number, int Size, int TotalItems, int TotalPages);

/// <summary>One page of a content list.</summary>
/// <param name="Type">Canonical key of the type that was listed.</param>
/// <param name="Items">The rows on this page, in the requested order.</param>
/// <param name="Page">Where this page sits in the full result set.</param>
public sealed record ContentListResponse(
    string Type,
    IReadOnlyList<ContentItemSummaryResponse> Items,
    PageInfo Page);

/// <summary>One content item in full.</summary>
/// <param name="Type">Canonical content type key.</param>
/// <param name="Key">Slug identifying the item within its type.</param>
/// <param name="Name">Display name, lifted out of <paramref name="Data"/> for convenience.</param>
/// <param name="Data">
/// The item exactly as it validates against its published JSON Schema at
/// <c>https://sw5e.com/schemas/{type}/v1.json</c>. Passed through unaltered, so
/// the schema is the contract for this object rather than anything restated
/// here.
/// </param>
public sealed record ContentItemResponse(
    string Type,
    string Key,
    string Name,
    JsonElement Data);

/// <summary>Results of a search across every content type.</summary>
/// <param name="Query">The search text, echoed back.</param>
/// <param name="TotalMatches">Items matched across all types, before any per-group cut.</param>
/// <param name="Groups">Matches grouped by content type, most relevant group first.</param>
public sealed record SearchResponse(
    string Query,
    int TotalMatches,
    IReadOnlyList<SearchGroupResponse> Groups);

/// <summary>Search results for one content type.</summary>
/// <param name="Type">Canonical content type key.</param>
/// <param name="Name">Singular display label.</param>
/// <param name="PluralName">Plural display label, for the group heading.</param>
/// <param name="RouteSegment">Slug the site uses in its own URLs.</param>
/// <param name="TotalMatches">
/// Items of this type that matched. May exceed <paramref name="Results"/>, which
/// is cut to the requested per-type limit, so the UI can offer "see all".
/// </param>
/// <param name="Results">The matches, most relevant first.</param>
public sealed record SearchGroupResponse(
    string Type,
    string Name,
    string PluralName,
    string RouteSegment,
    int TotalMatches,
    IReadOnlyList<SearchResultResponse> Results);

/// <summary>One search result, with the evidence for the match.</summary>
/// <param name="Item">The row to render.</param>
/// <param name="MatchedIn">
/// Which part of the item matched: <c>name</c>, <c>key</c>, <c>facet</c> or
/// <c>text</c>.
/// </param>
/// <param name="MatchedField">
/// The specific display field when <paramref name="MatchedIn"/> is
/// <c>facet</c>, such as <c>category</c>. Absent otherwise.
/// </param>
/// <param name="Snippet">
/// Plain text around the match, so the UI can show the phrase in context. Not
/// HTML: it is content-authored text and must be escaped when rendered.
/// </param>
/// <param name="Score">
/// Relevance, higher first. Comparable only within a single response.
/// </param>
public sealed record SearchResultResponse(
    ContentItemSummaryResponse Item,
    string MatchedIn,
    string? MatchedField,
    string Snippet,
    double Score)
{
    internal static SearchResultResponse From(SearchHit hit) =>
        new(ContentItemSummaryResponse.From(hit.Item),
            Describe(hit.MatchedField),
            hit.MatchedFieldName,
            hit.Snippet,
            Math.Round(hit.Score, 2));

    // Mapped explicitly rather than serialised from the enum so the generated
    // client sees a documented set of lowercase strings, and so renaming the
    // domain enum cannot silently change the wire format.
    private static string Describe(SearchMatchField field) => field switch
    {
        SearchMatchField.Name => "name",
        SearchMatchField.Key => "key",
        SearchMatchField.Facet => "facet",
        _ => "text",
    };
}
