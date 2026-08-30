using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sw5e.Domain.Content;

namespace Sw5e.Infrastructure.Content;

/// <summary>
/// An <see cref="IContentRepository"/> backed by schema-shaped JSON files on
/// disk, served from an index built once at startup.
/// </summary>
/// <remarks>
/// <para>
/// This is the stand-in for the PostgreSQL store, not a permanent home for the
/// content. It is written so the swap is a registration change: every filter,
/// order and cut arrives as part of the query, so each method here has a direct
/// SQL counterpart rather than a shape a database would have to emulate.
/// </para>
/// <para>
/// The index is immutable once built, so the instance is safe to share as a
/// singleton and every method completes synchronously.
/// </para>
/// </remarks>
public sealed class FileContentRepository : IContentRepository
{
    /// <summary>How much text either side of a body match a snippet carries.</summary>
    private const int SnippetWindow = 70;

    /// <summary>Unit separator, which cannot occur in a key or a query term.</summary>
    private const char Separator = '\u001f';

    private readonly ContentIndex _index;

    private FileContentRepository(ContentIndex index) => _index = index;

    /// <summary>
    /// Scans <paramref name="rootPath"/> and returns a repository over what it
    /// found, together with the item count and any warnings about files that
    /// were skipped.
    /// </summary>
    /// <remarks>
    /// Never throws for a missing, empty or partially populated directory: the
    /// content is maintained in a separate repository on its own schedule, so
    /// the API has to come up and serve whatever is present.
    /// </remarks>
    public static ContentLoadResult Load(string rootPath)
    {
        var result = ContentIndexBuilder.Build(rootPath);

        return new ContentLoadResult(
            new FileContentRepository(result.Index),
            result.ItemCount,
            result.Warnings);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentTypeDescriptor>> GetContentTypesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ContentTypeDescriptor> descriptors = ContentTypeRegistry.All
            .Select(definition => new ContentTypeDescriptor(
                definition.Key,
                definition.DisplayName,
                definition.PluralName,
                definition.RouteSegment,
                _index.Count(definition)))
            .ToArray();

        return Task.FromResult(descriptors);
    }

    /// <inheritdoc />
    public Task<PagedResult<ContentSummary>> ListAsync(
        ContentListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var nameFilter = query.NameContains?.Trim().ToLowerInvariant();

        var matching = _index.Items(query.Type).Where(item =>
            (string.IsNullOrEmpty(nameFilter) ||
             item.NameLower.Contains(nameFilter, StringComparison.Ordinal)) &&
            (query.SourceKey is null ||
             string.Equals(item.SourceKey, query.SourceKey, StringComparison.OrdinalIgnoreCase)) &&
            (query.ContentSet is null ||
             string.Equals(item.ContentSet, query.ContentSet, StringComparison.OrdinalIgnoreCase)));

        // Materialised once: the total and the page are drawn from the same
        // filtered set, which is what a database gets from one predicate used
        // by both a COUNT(*) and the paged SELECT.
        var filtered = matching.ToArray();

        var page = Sort(filtered, query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(item => item.ToSummary())
            .ToArray();

        var version = Version(
            query.Type.Key,
            nameFilter,
            query.SourceKey,
            query.ContentSet,
            query.SortBy.ToString(),
            query.Direction.ToString(),
            query.Page.ToString(CultureInfo.InvariantCulture),
            query.PageSize.ToString(CultureInfo.InvariantCulture));

        return Task.FromResult(new PagedResult<ContentSummary>(
            page,
            query.Page,
            query.PageSize,
            filtered.Length,
            version));
    }

    /// <inheritdoc />
    public Task<ContentDocument?> GetAsync(
        ContentTypeDefinition type,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        cancellationToken.ThrowIfCancellationRequested();

        // The endpoint rejects a malformed key before this is reached. Checking
        // again costs a regex match on a bounded string and means the store is
        // safe on its own terms rather than on a caller's promise.
        if (!ContentSlug.IsValid(key))
        {
            return Task.FromResult<ContentDocument?>(null);
        }

        var item = _index.Find(type, key);

        return Task.FromResult(item is null
            ? null
            : new ContentDocument(item.Type.Key, item.Key, item.Name, item.Version, item.Body));
    }

    /// <inheritdoc />
    public Task<ContentSearchResult> SearchAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var phrase = query.Text.Trim().ToLowerInvariant();
        var tokens = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var types = query.Types ?? ContentTypeRegistry.All;

        var groups = new List<SearchGroup>();
        var total = 0;

        foreach (var definition in ContentTypeRegistry.All)
        {
            if (!types.Any(candidate => string.Equals(candidate.Key, definition.Key, StringComparison.Ordinal)))
            {
                continue;
            }

            var hits = new List<SearchHit>();

            foreach (var item in _index.Items(definition))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hit = Match(item, phrase, tokens);

                if (hit is not null)
                {
                    hits.Add(hit);
                }
            }

            if (hits.Count == 0)
            {
                continue;
            }

            total += hits.Count;

            // Ranked then cut inside the group, which is the behaviour a
            // database implementation reproduces with row_number() partitioned
            // by type rather than by returning every match to be bucketed here.
            var ranked = hits
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.Item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(query.MaxPerType)
                .ToArray();

            groups.Add(new SearchGroup(
                definition.Key,
                definition.DisplayName,
                definition.PluralName,
                definition.RouteSegment,
                hits.Count,
                ranked));
        }

        // Most relevant type first, so a name match on a power outranks a body
        // mention of the same word in a monster's tactics paragraph.
        var ordered = groups
            .OrderByDescending(group => group.Hits.Count == 0 ? 0 : group.Hits[0].Score)
            .ThenByDescending(group => group.TotalMatches)
            .ThenBy(group => group.Type, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new ContentSearchResult(
            query.Text,
            total,
            ordered,
            Version(
                "search",
                phrase,
                string.Join(',', types.Select(type => type.Key)),
                query.MaxPerType.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>
    /// Scores one item against the query and explains where the match came
    /// from, or returns null when it does not match.
    /// </summary>
    /// <remarks>
    /// The tiers are ordered by how much a reader trusts the match: an exact
    /// name beats a prefix, which beats a name substring, which beats a slug,
    /// a display field, and finally the body prose. Only the strongest tier is
    /// reported, because a result row has room for one explanation.
    /// </remarks>
    private static SearchHit? Match(IndexedContentItem item, string phrase, string[] tokens)
    {
        if (phrase.Length == 0)
        {
            return null;
        }

        if (string.Equals(item.NameLower, phrase, StringComparison.Ordinal))
        {
            return Hit(item, SearchMatchField.Name, null, item.Name, 100);
        }

        var nameIndex = item.NameLower.IndexOf(phrase, StringComparison.Ordinal);

        if (nameIndex == 0)
        {
            return Hit(item, SearchMatchField.Name, null, item.Name, 85 + Coverage(phrase, item.NameLower));
        }

        if (nameIndex > 0)
        {
            return Hit(item, SearchMatchField.Name, null, item.Name, 70 + Coverage(phrase, item.NameLower));
        }

        if (item.Key.Contains(phrase, StringComparison.Ordinal))
        {
            return Hit(item, SearchMatchField.Key, null, item.Key, 55);
        }

        foreach (var facet in item.Facets)
        {
            if (facet.Value.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return Hit(item, SearchMatchField.Facet, facet.Key, facet.Value, 40);
            }
        }

        var textIndex = item.SearchTextLower.IndexOf(phrase, StringComparison.Ordinal);

        if (textIndex >= 0)
        {
            return Hit(
                item,
                SearchMatchField.Text,
                null,
                PlainText.Snippet(item.SearchText, textIndex, phrase.Length, SnippetWindow),
                25);
        }

        // Last resort for a multi-word query: every word present somewhere,
        // in any order. Ranked below any phrase match, because scattered words
        // are much weaker evidence than the phrase itself.
        if (tokens.Length > 1 && tokens.All(token =>
                item.SearchTextLower.Contains(token, StringComparison.Ordinal)))
        {
            var firstToken = item.SearchTextLower.IndexOf(tokens[0], StringComparison.Ordinal);

            return Hit(
                item,
                SearchMatchField.Text,
                null,
                PlainText.Snippet(item.SearchText, firstToken, tokens[0].Length, SnippetWindow),
                10);
        }

        return null;
    }

    private static SearchHit Hit(
        IndexedContentItem item,
        SearchMatchField field,
        string? fieldName,
        string snippet,
        double score) =>
        new(item.ToSummary(), field, fieldName, snippet, score);

    /// <summary>
    /// How much of the name the query accounts for, as a small tiebreaker.
    /// "sabre" against "Sabre" should outrank "sabre" against "Sabre of the
    /// Old Republic Ceremonial Guard".
    /// </summary>
    private static double Coverage(string phrase, string name) =>
        name.Length == 0 ? 0 : 10.0 * phrase.Length / name.Length;

    private static IEnumerable<IndexedContentItem> Sort(
        IReadOnlyList<IndexedContentItem> items,
        ContentListQuery query)
    {
        var ascending = query.Direction == SortDirection.Ascending;

        // Key is appended as the tiebreaker on every ordering: without a total
        // order, two requests for the same page can return different rows, and
        // an item can be shown twice or skipped entirely across a paged walk.
        return query.SortBy switch
        {
            ContentSortField.Key => ascending
                ? items.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(item => item.Key, StringComparer.OrdinalIgnoreCase),

            ContentSortField.SourceKey => ascending
                ? items.OrderBy(item => item.SourceKey, NullsLast)
                       .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(item => item.SourceKey, NullsLast)
                       .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase),

            ContentSortField.ContentSet => ascending
                ? items.OrderBy(item => item.ContentSet, NullsLast)
                       .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(item => item.ContentSet, NullsLast)
                       .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase),

            _ => ascending
                ? items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Orders absent values after present ones in an ascending sort, matching
    /// PostgreSQL's <c>NULLS LAST</c> default so the two stores agree.
    /// </summary>
    private static readonly IComparer<string?> NullsLast = Comparer<string?>.Create((left, right) =>
        (left, right) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => StringComparer.OrdinalIgnoreCase.Compare(left, right),
        });

    /// <summary>
    /// A version token for one response. The index version covers the content;
    /// the query parts cover which slice of it was asked for. Together they
    /// change whenever the response body would, which is the whole requirement
    /// for an ETag.
    /// </summary>
    private string Version(params string?[] parts)
    {
        var builder = new StringBuilder(_index.Version);

        foreach (var part in parts)
        {
            builder.Append('\u001f').Append(part ?? string.Empty);
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }
}

/// <summary>
/// What a content scan produced: the repository to serve from, how many items
/// it holds, and what was skipped along the way.
/// </summary>
/// <param name="Warnings">
/// Startup diagnostics naming files and paths. Log these; never return them,
/// because they disclose the server's filesystem layout.
/// </param>
public sealed record ContentLoadResult(
    FileContentRepository Repository,
    int ItemCount,
    IReadOnlyList<string> Warnings);
