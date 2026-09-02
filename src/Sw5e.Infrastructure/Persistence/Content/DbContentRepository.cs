using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Sw5e.Domain.Content;

// Aliased rather than imported. This file's own namespace ends in "Content",
// as do two others it needs types from, and an unqualified using would leave a
// reader guessing which "Content" any given name came from.
using PlainText = Sw5e.Infrastructure.Content.PlainText;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// An <see cref="IContentRepository"/> backed by PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Every method here answers with one round trip and returns only the rows the
/// caller asked for. That is the whole reason <see cref="IContentRepository"/>
/// takes filtering, ordering and cutting as query parameters rather than
/// offering a <c>GetAll</c>: the same interface that reads naturally over an
/// in-memory index also compiles to a single <c>WHERE ... ORDER BY ... LIMIT</c>
/// here, instead of dragging a table into memory to be filtered by LINQ.
/// </para>
/// <para>
/// <b>Behavioural parity with the file-backed store is a requirement, not a
/// coincidence.</b> The site is live against <c>FileContentRepository</c>, and
/// switching stores is meant to be one registration change. If the same request
/// returned rows in a different order, or matched a different set of names,
/// the swap would be a visible change to every user. The comments below mark
/// each place where reproducing .NET's semantics in SQL took a deliberate
/// choice — collation, wildcard escaping, null ordering — because each of those
/// is somewhere a straightforward translation would have silently diverged.
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton, like every other implementation
/// of this interface, so it takes a context factory rather than a context: a
/// <see cref="DbContext"/> is neither thread-safe nor long-lived, and capturing
/// one in a singleton is the classic way to turn a working application into an
/// intermittently failing one under concurrency.
/// </para>
/// </remarks>
public sealed class DbContentRepository(IDbContextFactory<Sw5eContentDbContext> contextFactory)
    : IContentRepository
{
    /// <summary>How much text either side of a body match a snippet carries.</summary>
    /// <remarks>Matches the file-backed store, because the snippet is user-visible.</remarks>
    private const int SnippetWindow = 70;

    /// <summary>
    /// Extra context fetched around a body match so the snippet can be cut in
    /// .NET rather than in SQL.
    /// </summary>
    /// <remarks>
    /// Anything larger than <see cref="SnippetWindow"/> makes the cut identical
    /// to the one the file-backed store performs over the whole field, because
    /// the window then always contains both boundaries the snippet needs to
    /// decide about. Fetching the whole search blob per hit would work too, and
    /// would move up to sixteen kilobytes per row for a phrase shown in a
    /// hundred and forty characters.
    /// </remarks>
    private const int SnippetFetchPadding = SnippetWindow + 40;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentTypeDescriptor>> GetContentTypesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        // One grouped count for the whole registry rather than fourteen counts,
        // and certainly rather than fourteen paged reads. This endpoint is on the
        // critical path of every page load, because the site's navigation is
        // built from it.
        var counts = await database.ContentItems
            .GroupBy(item => item.ContentType)
            .Select(group => new { Type = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Type, row => row.Count, StringComparer.Ordinal, cancellationToken);

        // Driven by the compiled registry, not by what the database happens to
        // hold, so a type with no rows is reported with a count of zero rather
        // than vanishing from the navigation.
        return [.. ContentTypeRegistry.All.Select(definition => new ContentTypeDescriptor(
            definition.Key,
            definition.DisplayName,
            definition.PluralName,
            definition.RouteSegment,
            counts.GetValueOrDefault(definition.Key)))];
    }

    /// <inheritdoc />
    public async Task<PagedResult<ContentSummary>> ListAsync(
        ContentListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        var filtered = Filter(database.ContentItems.AsNoTracking(), query);

        // Counted over the same predicate the page is drawn from, which is what
        // makes "page 3 of 12" describe the set the user is actually walking.
        var total = await filtered.CountAsync(cancellationToken);

        var rows = await Sort(filtered, query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(item => new SummaryRow(
                item.ContentType,
                item.ItemKey,
                item.Name,
                item.SourceKey,
                item.ContentSet,
                item.Summary,
                item.Facets,
                item.Version))
            .ToListAsync(cancellationToken);

        // The projection stops at these columns on purpose: a page of fifty
        // monsters would otherwise move a few hundred kilobytes of stat block
        // to render a table with four columns in it.
        return new PagedResult<ContentSummary>(
            [.. rows.Select(row => row.ToSummary())],
            query.Page,
            query.PageSize,
            total,
            ListVersion(query, total, rows));
    }

    /// <inheritdoc />
    public async Task<ContentDocument?> GetAsync(
        ContentTypeDefinition type,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Re-checked here rather than trusted from the endpoint, on the same
        // principle the file-backed store applies: a store that is only safe
        // because of what its callers promise is one refactor away from not
        // being safe. A key that is not a slug cannot name a row, because the
        // column carries the same check constraint.
        if (!ContentSlug.IsValid(key))
        {
            return null;
        }

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await database.ContentItems
            .AsNoTracking()
            .Where(item => item.ContentType == type.Key && item.ItemKey == key)
            .Select(item => new { item.ItemKey, item.Name, item.Version, item.Body })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        // Cloned off the document so the element outlives the JsonDocument it
        // was parsed from and can be serialised after this method returns.
        using var document = JsonDocument.Parse(row.Body);

        return new ContentDocument(
            type.Key,
            row.ItemKey,
            row.Name,
            row.Version,
            document.RootElement.Clone());
    }

    /// <inheritdoc />
    public async Task<ContentSearchResult> SearchAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var phrase = query.Text.Trim().ToLowerInvariant();
        var tokens = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var types = query.Types ?? ContentTypeRegistry.All;
        var typeKeys = types.Select(type => type.Key).ToArray();

        if (phrase.Length == 0 || typeKeys.Length == 0)
        {
            return new ContentSearchResult(
                query.Text, 0, [], SearchVersion(query, phrase, typeKeys, 0, []));
        }

        var hits = await ExecuteSearchAsync(phrase, tokens, typeKeys, query.MaxPerType, cancellationToken);

        var groups = new List<SearchGroup>();
        var total = 0;

        // Walked in registry order so a tie between two groups resolves the
        // same way it does in the file-backed store.
        foreach (var definition in ContentTypeRegistry.All)
        {
            var forType = hits.Where(hit => string.Equals(hit.Type, definition.Key, StringComparison.Ordinal)).ToArray();

            if (forType.Length == 0)
            {
                continue;
            }

            // The window function counted the whole match set for the type;
            // what came back is only the requested slice of it, which is what
            // lets the UI say "showing 5 of 41 powers".
            total += forType[0].TypeTotal;

            groups.Add(new SearchGroup(
                definition.Key,
                definition.DisplayName,
                definition.PluralName,
                definition.RouteSegment,
                forType[0].TypeTotal,
                [.. forType.Select(hit => hit.ToHit())]));
        }

        var ordered = groups
            .OrderByDescending(group => group.Hits.Count == 0 ? 0 : group.Hits[0].Score)
            .ThenByDescending(group => group.TotalMatches)
            .ThenBy(group => group.Type, StringComparer.Ordinal)
            .ToArray();

        return new ContentSearchResult(
            query.Text,
            total,
            ordered,
            SearchVersion(query, phrase, typeKeys, total, hits));
    }

    /// <summary>
    /// The list predicate, expressed so PostgreSQL can use an index for every
    /// clause.
    /// </summary>
    private static IQueryable<ContentItemRow> Filter(
        IQueryable<ContentItemRow> items,
        ContentListQuery query)
    {
        items = items.Where(item => item.ContentType == query.Type.Key);

        var nameFilter = query.NameContains?.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(nameFilter))
        {
            // LIKE rather than the more obvious string.Contains, which Npgsql
            // translates to strpos() — correct, but not something a trigram
            // index can serve, so every name filter would scan the type.
            //
            // The filter is escaped first. Without that, a caller searching for
            // a literal "%" matches every row and a caller searching for "_"
            // matches every single-character name: LIKE metacharacters in
            // caller-supplied text. That is not an injection — the value is a
            // parameter either way — but it is the same class of mistake, and
            // it is a real behaviour difference from the file-backed store,
            // which treats the filter as a plain substring.
            var pattern = $"%{EscapeLikePattern(nameFilter)}%";

            items = items.Where(item => EF.Functions.Like(item.NameLower, pattern, LikeEscape));
        }

        // Compared against the stored value directly rather than through
        // lower() on both sides. Both columns hold values the JSON Schemas
        // constrain to lowercase — source keys are slugs, content sets are an
        // enum of two lowercase strings — so folding the caller's input is
        // enough to reproduce the file-backed store's case-insensitive match,
        // and it leaves the column bare so the index on it is usable.
        if (query.SourceKey is { } sourceKey)
        {
            var normalised = sourceKey.ToLowerInvariant();
            items = items.Where(item => item.SourceKey == normalised);
        }

        if (query.ContentSet is { } contentSet)
        {
            var normalised = contentSet.ToLowerInvariant();
            items = items.Where(item => item.ContentSet == normalised);
        }

        return items;
    }

    /// <summary>
    /// The list ordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every ordering ends with the item key, because without a total order two
    /// requests for the same page may return different rows: an item can appear
    /// on two pages or on none, and the bug only shows up once there is enough
    /// content for a second page.
    /// </para>
    /// <para>
    /// The ordering columns are declared <c>COLLATE "C"</c> in the model, so
    /// PostgreSQL compares them byte by byte. That is not a micro-optimisation,
    /// it is the parity requirement: under a locale collation such as
    /// <c>en_US.utf8</c>, punctuation is weighted differently or ignored
    /// outright, so "Twi'lek" sorts in a different place than .NET's ordinal
    /// comparison puts it, and the same page of species comes back in a
    /// different order from the two stores. Byte order is the only collation
    /// that agrees with <see cref="StringComparer.Ordinal"/>.
    /// </para>
    /// <para>
    /// Null ordering needs no explicit clause: PostgreSQL defaults to NULLS
    /// LAST ascending and NULLS FIRST descending, which is exactly what the
    /// file-backed store's null-last comparer produces when it is reversed.
    /// </para>
    /// </remarks>
    private static IQueryable<ContentItemRow> Sort(
        IQueryable<ContentItemRow> items,
        ContentListQuery query)
    {
        var ascending = query.Direction == SortDirection.Ascending;

        return query.SortBy switch
        {
            ContentSortField.Key => ascending
                ? items.OrderBy(item => item.ItemKey)
                : items.OrderByDescending(item => item.ItemKey),

            ContentSortField.SourceKey => ascending
                ? items.OrderBy(item => item.SourceKey).ThenBy(item => item.ItemKey)
                : items.OrderByDescending(item => item.SourceKey).ThenBy(item => item.ItemKey),

            ContentSortField.ContentSet => ascending
                ? items.OrderBy(item => item.ContentSet).ThenBy(item => item.ItemKey)
                : items.OrderByDescending(item => item.ContentSet).ThenBy(item => item.ItemKey),

            // Ordered on the folded copy rather than on `name` itself, because
            // the file-backed store orders case-insensitively. The two agree
            // for every character below 'A' — every space, apostrophe, comma,
            // hyphen and parenthesis the corpus actually contains — and would
            // differ only for a name containing one of the six characters that
            // sit between 'Z' and 'a'. The parity test over the fixture is what
            // holds that assumption honest.
            _ => ascending
                ? items.OrderBy(item => item.NameLower).ThenBy(item => item.ItemKey)
                : items.OrderByDescending(item => item.NameLower).ThenBy(item => item.ItemKey),
        };
    }

    /// <summary>
    /// Runs the ranked, grouped, per-type-windowed search in one statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the query <see cref="IContentRepository.SearchAsync"/>'s remarks
    /// describe: <c>row_number()</c> partitioned by content type, so each group
    /// is ranked and cut inside the database. The alternative — fetching every
    /// match and bucketing them in memory — over-fetches by however many types
    /// the results cluster into, which for a one-word query against a corpus
    /// where the same word appears in prose across fourteen types is most of the
    /// catalogue.
    /// </para>
    /// <para>
    /// Written as SQL rather than LINQ because it is not a query LINQ expresses:
    /// the scoring ladder is a seven-arm CASE that has to be evaluated once and
    /// referenced three times, and the window functions have to see the score.
    /// A LINQ version would be less readable and would still be this SQL.
    /// </para>
    /// <para>
    /// Every caller-supplied value below is a parameter. The only interpolated
    /// text is the padding constant, which is a compile-time integer.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<SearchRow>> ExecuteSearchAsync(
        string phrase,
        string[] tokens,
        string[] typeKeys,
        int maxPerType,
        CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = database.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = SearchSql;

        AddParameter(command, "phrase", NpgsqlDbType.Text, phrase);
        AddParameter(command, "pattern", NpgsqlDbType.Text, $"%{EscapeLikePattern(phrase)}%");
        AddParameter(command, "types", NpgsqlDbType.Array | NpgsqlDbType.Text, typeKeys);
        AddParameter(command, "max_per_type", NpgsqlDbType.Integer, maxPerType);

        // The multi-word fallback: every word present somewhere, in any order.
        // Single-word queries must not reach it, or a phrase that already
        // failed every tier above would match itself here and be scored as if
        // it were a scattered-word hit.
        AddParameter(command, "multi_token", NpgsqlDbType.Boolean, tokens.Length > 1);
        AddParameter(
            command,
            "token_patterns",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            tokens.Select(token => $"%{EscapeLikePattern(token)}%").ToArray());
        AddParameter(command, "first_token", NpgsqlDbType.Text, tokens.Length > 0 ? tokens[0] : phrase);
        AddParameter(command, "padding", NpgsqlDbType.Integer, SnippetFetchPadding);

        await database.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var rows = new List<SearchRow>();

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(SearchRow.Read(reader));
            }

            return rows;
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// The scoring ladder and the per-type window, in one statement.
    /// </summary>
    /// <remarks>
    /// The tiers are the file-backed store's, in the same order and with the
    /// same numbers: an exact name beats a name prefix, which beats a name
    /// substring, which beats the slug, a display field, the body prose, and
    /// finally scattered words. Only the strongest tier is reported, because a
    /// result row has room for one explanation of why it is there.
    /// </remarks>
    private const string SearchSql =
        """
        WITH matched AS (
            SELECT
                i.content_type,
                i.item_key,
                i.name,
                i.source_key,
                i.content_set,
                i.summary,
                i.facets,
                i.version,
                i.name_lower,
                i.search_text,
                position(@phrase IN i.name_lower)        AS name_pos,
                position(@phrase IN i.item_key)          AS key_pos,
                position(@phrase IN i.search_text_lower) AS text_pos,
                -- Headings are scored above the prose around them; see the
                -- ladder below and ContentProjection.HeadingText.
                position(@phrase IN i.heading_text_lower) AS heading_pos,
                position(@first_token IN i.search_text_lower) AS token_pos,
                length(i.search_text)                    AS text_length,
                -- Ordered under the C collation so the field reported as the
                -- explanation is the same one the file-backed store reports.
                -- jsonb_each_text returns text in the database's default
                -- collation, which is the host's locale, so an unqualified
                -- ORDER BY here would pick a different field on a different
                -- machine when a query matches two of them.
                (
                    SELECT f.key
                    FROM jsonb_each_text(i.facets) AS f
                    WHERE position(@phrase IN lower(f.value)) > 0
                    ORDER BY f.key COLLATE "C"
                    LIMIT 1
                ) AS facet_key,
                (
                    SELECT f.value
                    FROM jsonb_each_text(i.facets) AS f
                    WHERE position(@phrase IN lower(f.value)) > 0
                    ORDER BY f.key COLLATE "C"
                    LIMIT 1
                ) AS facet_value
            FROM content.content_item AS i
            WHERE i.content_type = ANY(@types)
              -- No ESCAPE clause: backslash is already PostgreSQL's default
              -- LIKE escape character, and LIKE ALL takes no ESCAPE clause, so
              -- spelling it out on the others would only suggest that the one
              -- without it behaves differently.
              AND (
                    i.name_lower        LIKE @pattern
                 OR i.item_key          LIKE @pattern
                 OR i.search_text_lower LIKE @pattern
                 -- Almost always redundant, because a heading's words are part
                 -- of the prose this harvests from. Not quite always: the
                 -- search text is capped, so a heading past the cap is in one
                 -- column and not the other, and a document is not going to be
                 -- unfindable by its own section title over a size limit.
                 OR i.heading_text_lower LIKE @pattern
                 OR EXISTS (
                        SELECT 1
                        FROM jsonb_each_text(i.facets) AS f
                        WHERE lower(f.value) LIKE @pattern
                    )
                 OR (@multi_token AND i.search_text_lower LIKE ALL(@token_patterns))
              )
        ),
        scored AS (
            SELECT
                m.*,
                CASE
                    WHEN m.name_lower = @phrase   THEN 1
                    WHEN m.name_pos > 0           THEN 2
                    WHEN m.key_pos > 0            THEN 3
                    WHEN m.facet_key IS NOT NULL  THEN 4
                    WHEN m.heading_pos > 0        THEN 5
                    WHEN m.text_pos > 0           THEN 6
                    ELSE 7
                END AS tier
            FROM matched AS m
        ),
        weighted AS (
            SELECT
                s.*,
                CASE s.tier
                    WHEN 1 THEN 100.0
                    WHEN 2 THEN
                        (CASE WHEN s.name_pos = 1 THEN 85.0 ELSE 70.0 END)
                        + 10.0 * length(@phrase) / GREATEST(length(s.name_lower), 1)
                    WHEN 3 THEN 55.0
                    WHEN 4 THEN 40.0
                    -- Below a curated display field, which is a structured
                    -- statement about what a document is, and above the prose,
                    -- which is only somewhere the words appear.
                    WHEN 5 THEN 35.0
                    WHEN 6 THEN 25.0
                    ELSE 10.0
                END AS score,
                -- A heading match still quotes the prose around the phrase, and
                -- can: a heading's words are part of the text this cuts from.
                CASE s.tier
                    WHEN 5 THEN s.text_pos
                    WHEN 6 THEN s.text_pos
                    WHEN 7 THEN s.token_pos
                    ELSE 0
                END AS snippet_pos,
                CASE s.tier
                    WHEN 5 THEN length(@phrase)
                    WHEN 6 THEN length(@phrase)
                    WHEN 7 THEN length(@first_token)
                    ELSE 0
                END AS snippet_length
            FROM scored AS s
        ),
        ranked AS (
            SELECT
                w.*,
                COUNT(*)     OVER (PARTITION BY w.content_type) AS type_total,
                ROW_NUMBER() OVER (
                    PARTITION BY w.content_type
                    ORDER BY w.score DESC, w.name_lower ASC, w.item_key ASC
                ) AS rank
            FROM weighted AS w
        )
        SELECT
            r.content_type,
            r.item_key,
            r.name,
            r.source_key,
            r.content_set,
            r.summary,
            r.facets,
            r.version,
            r.tier,
            r.score,
            r.type_total,
            r.facet_key,
            r.facet_value,
            CASE
                WHEN r.snippet_pos > 0
                THEN substring(r.search_text FROM GREATEST(1, r.snippet_pos - @padding)
                                             FOR r.snippet_length + (2 * @padding))
                ELSE ''
            END AS snippet_window,
            CASE
                WHEN r.snippet_pos > 0 THEN r.snippet_pos - GREATEST(1, r.snippet_pos - @padding)
                ELSE 0
            END AS snippet_offset,
            r.snippet_length
        FROM ranked AS r
        WHERE r.rank <= @max_per_type
        ORDER BY r.content_type, r.rank
        """;

    /// <summary>Escape character used with every LIKE in this class.</summary>
    private const string LikeEscape = "\\";

    /// <summary>
    /// Makes caller-supplied text match literally under LIKE.
    /// </summary>
    /// <remarks>
    /// The backslash is escaped first; escaping it after the metacharacters
    /// would double the escapes this method itself introduced and turn "50%"
    /// into a pattern that matches nothing.
    /// </remarks>
    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    private static void AddParameter(DbCommand command, string name, NpgsqlDbType type, object value)
    {
        var parameter = new NpgsqlParameter(name, type) { Value = value };
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// A version token for one list response.
    /// </summary>
    /// <remarks>
    /// Built from the query that was asked and the exact rows that answered it,
    /// including each row's own content version. That is a stronger validator
    /// than a catalogue-wide token: it changes when and only when this
    /// response's body would, so an edit to one monster does not invalidate
    /// every client's cached page of species, and an edit to a row on this page
    /// certainly does.
    /// </remarks>
    private static string ListVersion(ContentListQuery query, int total, List<SummaryRow> rows)
    {
        var builder = new StringBuilder("list");

        Append(builder, query.Type.Key);
        Append(builder, query.NameContains?.Trim().ToLowerInvariant());
        Append(builder, query.SourceKey);
        Append(builder, query.ContentSet);
        Append(builder, query.SortBy.ToString());
        Append(builder, query.Direction.ToString());
        Append(builder, query.Page.ToString(CultureInfo.InvariantCulture));
        Append(builder, query.PageSize.ToString(CultureInfo.InvariantCulture));
        Append(builder, total.ToString(CultureInfo.InvariantCulture));

        foreach (var row in rows)
        {
            Append(builder, $"{row.ItemKey}:{row.Version}");
        }

        return Hash(builder.ToString());
    }

    /// <summary>
    /// A version token for one search response.
    /// </summary>
    /// <remarks>
    /// Covers the query, the total, and every returned hit together with the
    /// version of the document it came from. Including the document versions is
    /// what makes an edit to a monster's prose change the ETag of the search
    /// that quotes it: the snippet is part of the response body, so a validator
    /// built only from keys would tell a client its stale snippet is current.
    /// </remarks>
    private static string SearchVersion(
        ContentSearchQuery query,
        string phrase,
        string[] typeKeys,
        int total,
        IReadOnlyList<SearchRow> hits)
    {
        var builder = new StringBuilder("search");

        Append(builder, phrase);
        Append(builder, string.Join(',', typeKeys));
        Append(builder, query.MaxPerType.ToString(CultureInfo.InvariantCulture));
        Append(builder, total.ToString(CultureInfo.InvariantCulture));

        foreach (var hit in hits)
        {
            Append(builder, $"{hit.Type}/{hit.ItemKey}:{hit.Version}");
        }

        return Hash(builder.ToString());
    }

    /// <summary>
    /// Appends one part of a version token, preceded by a separator.
    /// </summary>
    /// <remarks>
    /// The separator is what stops two different queries hashing to the same
    /// token. Without one, "ab" followed by "c" and "a" followed by "bc"
    /// produce identical input, and therefore an ETag that tells a client two
    /// different responses are the same response.
    /// </remarks>
    private static void Append(StringBuilder builder, string? part) =>
        builder.Append('').Append(part ?? string.Empty);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>The columns a list row is built from.</summary>
    private sealed record SummaryRow(
        string ContentType,
        string ItemKey,
        string Name,
        string? SourceKey,
        string? ContentSet,
        string? Summary,
        string Facets,
        string Version)
    {
        public ContentSummary ToSummary() =>
            new(ContentType, ItemKey, Name, SourceKey, ContentSet, Summary, ReadFacets(Facets));
    }

    /// <summary>
    /// One row of the search result set, as the reader returns it.
    /// </summary>
    /// <remarks>
    /// Read by ordinal rather than by name because the SELECT list is in this
    /// file, a dozen lines above: a name lookup per column per row would buy
    /// resilience against a change that cannot happen without editing both
    /// halves at once.
    /// </remarks>
    private sealed record SearchRow(
        string Type,
        string ItemKey,
        string Name,
        string? SourceKey,
        string? ContentSet,
        string? Summary,
        string Facets,
        string Version,
        int Tier,
        double Score,
        int TypeTotal,
        string? FacetKey,
        string? FacetValue,
        string SnippetText,
        int SnippetOffset,
        int SnippetLength)
    {
        public static SearchRow Read(DbDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),

            // The scoring CASE is numeric in PostgreSQL because its arms are
            // numeric literals; the domain carries relevance as a double, and
            // the response rounds it to two places either way.
            (double)reader.GetDecimal(9),

            // COUNT(*) OVER () is bigint. A content type with more than two
            // billion matching items is not a case worth carrying a long for.
            (int)reader.GetInt64(10),

            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetString(13),
            reader.GetInt32(14),
            reader.GetInt32(15));

        public SearchHit ToHit()
        {
            var item = new ContentSummary(
                Type, ItemKey, Name, SourceKey, ContentSet, Summary, ReadFacets(Facets));

            // Tiers 1 and 2 are name matches, 3 the slug, 4 a display field,
            // 5 a heading, and 6 and 7 the body prose. The snippet is whatever
            // the reader needs to see to understand the match: the value that
            // matched for the first four, and the phrase in context for the
            // rest — including a heading, whose words are part of the prose the
            // window is cut from.
            return Tier switch
            {
                1 or 2 => new SearchHit(item, SearchMatchField.Name, null, Name, Score),
                3 => new SearchHit(item, SearchMatchField.Key, null, ItemKey, Score),
                4 => new SearchHit(item, SearchMatchField.Facet, FacetKey, FacetValue ?? string.Empty, Score),
                5 => new SearchHit(
                    item,
                    SearchMatchField.Heading,
                    null,
                    PlainText.Snippet(SnippetText, SnippetOffset, SnippetLength, SnippetWindow),
                    Score),

                // Cut here rather than in SQL so the snippet is produced by the
                // same function the file-backed store uses. The fetched window
                // is wider than the snippet on both sides, which makes this cut
                // identical to one taken over the whole field.
                _ => new SearchHit(
                    item,
                    SearchMatchField.Text,
                    null,
                    PlainText.Snippet(SnippetText, SnippetOffset, SnippetLength, SnippetWindow),
                    Score),
            };
        }
    }

    /// <summary>
    /// Reads the projected display fields out of their jsonb column.
    /// </summary>
    /// <remarks>
    /// Sorted, because jsonb returns members in its own internal order — by key
    /// length, then by bytes — and the file-backed store produces them sorted
    /// by name. Nothing reads these positionally, but the two stores emitting
    /// the same object in a different member order would be a gratuitous
    /// difference in the response body.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ReadFacets(string json)
    {
        var facets = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        return facets is null
            ? new SortedDictionary<string, string>(StringComparer.Ordinal)
            : new SortedDictionary<string, string>(facets, StringComparer.Ordinal);
    }
}
