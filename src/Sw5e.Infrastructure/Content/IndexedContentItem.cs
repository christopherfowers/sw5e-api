using System.Text.Json;
using Sw5e.Domain.Content;

namespace Sw5e.Infrastructure.Content;

/// <summary>
/// One content item as held in the in-memory index: the projected row, the
/// body, and the precomputed strings the query paths need.
/// </summary>
/// <remarks>
/// <para>
/// The lowercased fields exist so that neither listing nor searching allocates
/// a lowercased copy of every candidate on every request. They are the
/// in-memory counterpart of the expression indexes a database implementation
/// would create — <c>lower(name)</c> for the name filter, a full-text vector
/// for the search blob.
/// </para>
/// <para>
/// <see cref="Body"/> is a cloned <see cref="JsonElement"/>, which detaches it
/// from the <see cref="JsonDocument"/> it was parsed from. That makes it safe
/// to hold for the process lifetime and to hand to any number of concurrent
/// readers.
/// </para>
/// </remarks>
internal sealed class IndexedContentItem
{
    public required ContentTypeDefinition Type { get; init; }

    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required JsonElement Body { get; init; }

    public string? SourceKey { get; init; }

    public string? ContentSet { get; init; }

    public string? Summary { get; init; }

    public required IReadOnlyDictionary<string, string> Facets { get; init; }

    /// <summary>Prose harvested from the whole document, for free-text matching.</summary>
    public required string SearchText { get; init; }

    public required string NameLower { get; init; }

    public required string SearchTextLower { get; init; }

    /// <summary>
    /// The document's markdown headings, lowercased, one per line.
    /// </summary>
    /// <remarks>
    /// Carried separately from <see cref="SearchTextLower"/> so search can rank
    /// a heading above a sentence. See <c>ContentProjection.HeadingText</c>.
    /// </remarks>
    public required string HeadingTextLower { get; init; }

    private ContentSummary? _summaryRow;

    /// <summary>
    /// The row projection, built once. Rows are immutable and shared across
    /// requests, so there is no reason to rebuild one per response.
    /// </summary>
    public ContentSummary ToSummary() =>
        _summaryRow ??= new ContentSummary(
            Type.Key,
            Key,
            Name,
            SourceKey,
            ContentSet,
            Summary,
            Facets);
}
