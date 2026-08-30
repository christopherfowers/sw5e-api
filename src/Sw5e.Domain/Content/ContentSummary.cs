namespace Sw5e.Domain.Content;

/// <summary>
/// The projection a list row or a search result row is rendered from: enough
/// to draw the row and link to the item, and nothing more.
/// </summary>
/// <remarks>
/// This is a projection rather than the whole document on purpose. A monster
/// document runs to kilobytes of stat block; a list of fifty of them would move
/// megabytes to render a table the user reads four columns of. A database
/// implementation selects exactly these columns (plus a jsonb projection for
/// <see cref="Facets"/>) instead of the document body.
/// </remarks>
/// <param name="Type">Canonical content type key.</param>
/// <param name="Key">Slug identifying the item within its type.</param>
/// <param name="Name">Display name.</param>
/// <param name="SourceKey">Publication the item came from, when it records one.</param>
/// <param name="ContentSet">"core" or "expanded-content", when the type records it.</param>
/// <param name="Summary">One-line plain-text description, already truncated.</param>
/// <param name="Facets">
/// The handful of type-specific display fields a row needs, such as a power's
/// level or a monster's challenge rating. Kept as a string map so one row shape
/// serves all nine types.
/// </param>
public sealed record ContentSummary(
    string Type,
    string Key,
    string Name,
    string? SourceKey,
    string? ContentSet,
    string? Summary,
    IReadOnlyDictionary<string, string> Facets);
