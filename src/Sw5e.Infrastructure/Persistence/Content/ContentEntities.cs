using NpgsqlTypes;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// One row of the content type registry, mirrored into the database.
/// </summary>
/// <remarks>
/// The authoritative registry is <see cref="Domain.Content.ContentTypeRegistry"/>.
/// This table is seeded from it by a migration so that
/// <c>content_item.content_type</c> can carry a foreign key; without it the
/// column is free text and a bad import can file a row under a type the API
/// never looks for. Labels are carried so the schema reads sensibly in
/// <c>psql</c>, but the API answers from the compiled list.
/// </remarks>
public sealed class ContentTypeRow
{
    /// <summary>Canonical type key, matching <c>ContentTypeDefinition.Key</c>.</summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    public required string PluralName { get; set; }

    /// <summary>Slug the site uses in its own URLs.</summary>
    public required string RouteSegment { get; set; }

    /// <summary>Position in the site's navigation, matching the registry's order.</summary>
    public required int SortOrder { get; set; }
}

/// <summary>
/// One content document: its identity, the projected columns lists and search
/// filter and order on, and the document itself.
/// </summary>
/// <remarks>
/// <para>
/// One table with a jsonb body rather than a table per type. The types have
/// almost nothing in common below the surface, and the published contract for
/// <c>GET /api/content/{type}/{key}</c> is that the body <em>is</em> the
/// schema-validated document. A shredded model would have to reassemble that,
/// which writes the schema down twice with nothing keeping the two equal.
/// </para>
/// <para>
/// The columns a query touches are lifted out of the document, and
/// cross-document links into <see cref="ContentReferenceRow"/>. They are a
/// projection rather than a second copy: derived from <see cref="Body"/> on
/// every write by the same code the file-backed store uses, so re-running the
/// importer rebuilds them.
/// </para>
/// </remarks>
public sealed class ContentItemRow
{
    /// <summary>
    /// Surrogate key. The domain identity is <see cref="ContentType"/> plus
    /// <see cref="ItemKey"/>, which carries a unique constraint. This exists so
    /// the reference table has something narrow to point at, and so renaming a
    /// slug is one update rather than a cascade.
    /// </summary>
    public long Id { get; set; }

    /// <summary>Canonical content type key. Foreign key to the type registry.</summary>
    public required string ContentType { get; set; }

    /// <summary>
    /// The item's slug, unique within its type. Named <c>ItemKey</c> because
    /// <c>key</c> reads as the primary key when scanning SQL.
    /// </summary>
    public required string ItemKey { get; set; }

    /// <summary>Display name, lifted from the document's <c>name</c> or <c>title</c>.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Publication the item came from, or null on types that record none.
    /// </summary>
    /// <remarks>
    /// A filter column on the hot list query, so it sits where the predicate
    /// can reach it without a join. Also written as a reference row, which is
    /// what makes "everything in the Player's Handbook" answerable across every
    /// type at once.
    /// </remarks>
    public string? SourceKey { get; set; }

    /// <summary>Core versus expanded content, or null on types that record neither.</summary>
    public string? ContentSet { get; set; }

    /// <summary>One-line plain-text description for a list row, already truncated.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Type-specific display fields for a list row, as a jsonb object of
    /// strings.
    /// </summary>
    /// <remarks>
    /// Presentation, not data: nothing filters or sorts on these. A child table
    /// would turn every list page into a join fan-out for values that are only
    /// displayed.
    /// </remarks>
    public required string Facets { get; set; }

    /// <summary>
    /// The document, exactly as it validates against its published JSON Schema.
    /// </summary>
    /// <remarks>
    /// jsonb rather than json, so the operators and indexes work. The cost is
    /// normalisation: member order is not preserved and duplicate members
    /// collapse to the last. Neither is significant in JSON, and no consumer
    /// depends on member order.
    /// </remarks>
    public required string Body { get; set; }

    /// <summary>
    /// Every piece of prose in the document, flattened for free-text matching.
    /// </summary>
    /// <remarks>
    /// Materialised rather than extracted from <see cref="Body"/> per query,
    /// which would be a function call per row per request over a value that
    /// changes only at import.
    /// </remarks>
    public required string SearchText { get; set; }

    /// <summary>
    /// Opaque token that changes when <see cref="Body"/> does, used as the ETag
    /// validator.
    /// </summary>
    /// <remarks>
    /// A content hash rather than a timestamp: re-importing an unchanged corpus
    /// must not invalidate every client's cache, and a redeploy does exactly
    /// that.
    /// </remarks>
    public required string Version { get; set; }

    /// <summary>
    /// <see cref="Name"/> lowercased, for the case-insensitive name filter.
    /// </summary>
    /// <remarks>
    /// Stored rather than <c>lower(name)</c> or <c>ILIKE</c>, both of which
    /// make the result depend on the database's case-folding rather than
    /// .NET's. The file-backed store folds with <c>ToLowerInvariant</c>;
    /// folding once at import with the same call is what makes the two stores
    /// agree beyond ASCII.
    /// </remarks>
    public required string NameLower { get; set; }

    /// <summary><see cref="SearchText"/> lowercased, for the same reason.</summary>
    public required string SearchTextLower { get; set; }

    /// <summary>
    /// Just the headings in the document's prose, lowercased, one per line.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SearchTextLower"/> so search can rank a heading
    /// above a sentence. Without it every prose match sat in one tier and
    /// ordering fell back to the alphabet: "difficult terrain" returned
    /// twenty-nine class features ahead of the chapter named after the phrase.
    /// Only the folded form is stored, because a heading match reports the
    /// heading itself rather than a snippet cut from prose.
    /// </remarks>
    public required string HeadingTextLower { get; set; }

    /// <summary>
    /// The document as PostgreSQL full text search sees it, weighted by where
    /// each word was found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Weights are <c>A</c> for the name, <c>B</c> for a heading, <c>D</c> for
    /// the body. A stored generated column, so it cannot drift from the text it
    /// summarises and no write path can forget it.
    /// </para>
    /// <para>
    /// This orders prose beneath the trigram ladder rather than replacing it.
    /// Full text search is word-based, so it cannot find "Acrobat" from "acro"
    /// or connect "blast" to "blaster", which in this corpus is most of the
    /// weapons.
    /// </para>
    /// </remarks>
    public NpgsqlTsVector? SearchVector { get; set; }

    /// <summary>When the row was first imported.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the row's body last changed. Not touched by a no-op import.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Links from this item to other content items.</summary>
    public ICollection<ContentReferenceRow> References { get; } = [];
}

/// <summary>
/// How a reference names the thing it points at.
/// </summary>
/// <remarks>
/// Both kinds exist because the corpus uses both. Exactly one field points at
/// another item by slug, <c>sourceKey</c>; everything else names its target by
/// display name, because the documents were transcribed from print.
/// </remarks>
public enum ContentReferenceTargetKind
{
    /// <summary>The target is named by its slug, matched against <c>item_key</c>.</summary>
    Key,

    /// <summary>
    /// The target is named by its display name, matched against <c>name</c>.
    /// </summary>
    /// <remarks>
    /// Names are not unique across the corpus, so a name match can be
    /// ambiguous. The importer resolves one only when exactly one candidate of
    /// the target type matches, rather than picking arbitrarily and inventing
    /// an edge that is probably wrong.
    /// </remarks>
    Name,
}

/// <summary>
/// One directed link from a content item to another, resolved where possible
/// and recorded as intent where not.
/// </summary>
/// <remarks>
/// <para>
/// Edges are rows so that traversal is a join rather than one round trip per
/// edge with every type's link fields hard-coded into the walker. That is what
/// makes questions like "everything the Wookiee species grants" answerable.
/// </para>
/// <para>
/// An unresolved edge is a row rather than an error. Three target types the
/// corpus refers to do not exist yet, and plenty of named targets have not been
/// written, so refusing them would mean the database could only hold a finished
/// corpus. Recording the intent separately from the resolution turns "what is
/// this corpus still missing" into a query.
/// </para>
/// <para>
/// <see cref="ResolvedItemId"/> is a real foreign key, so traversal is an index
/// lookup the database guarantees. <see cref="TargetType"/> and
/// <see cref="TargetIdentifier"/> carry no such constraint, because
/// constraining them is what would make an in-progress corpus unimportable.
/// </para>
/// </remarks>
public sealed class ContentReferenceRow
{
    public long Id { get; set; }

    /// <summary>The item the link was found in.</summary>
    public long FromItemId { get; set; }

    /// <summary>Navigation to the owning item.</summary>
    public ContentItemRow? FromItem { get; set; }

    /// <summary>
    /// What kind of link this is, such as <c>source</c>, <c>grantedBy</c> or
    /// <c>prerequisitePower</c>. Drawn from the closed set in
    /// <c>ContentReferenceMap</c>, never from document content.
    /// </summary>
    public required string Relation { get; set; }

    /// <summary>
    /// Where in the document the link was found, as a JSON path such as
    /// <c>$.featOptions[3].name</c>. Unique per item and relation, which makes
    /// re-importing idempotent for edges too.
    /// </summary>
    public required string JsonPath { get; set; }

    /// <summary>
    /// Type of content the link points at. Usually a registered type, but
    /// deliberately not constrained to one: <c>class</c> is referenced by every
    /// archetype and does not exist yet.
    /// </summary>
    public required string TargetType { get; set; }

    /// <summary>Whether <see cref="TargetIdentifier"/> is a slug or a display name.</summary>
    public required ContentReferenceTargetKind TargetKind { get; set; }

    /// <summary>
    /// The slug or display name the document gave, verbatim after trimming.
    /// Kept even when the target resolves, so edges can be re-resolved after
    /// missing content is authored without re-reading every document.
    /// </summary>
    public required string TargetIdentifier { get; set; }

    /// <summary>
    /// The item this edge reaches, or null when nothing matches or a name match
    /// was ambiguous.
    /// </summary>
    public long? ResolvedItemId { get; set; }

    /// <summary>Navigation to the resolved target.</summary>
    public ContentItemRow? ResolvedItem { get; set; }

    /// <summary>
    /// Position among links of the same relation on the same item, so an
    /// ordered list in the document stays ordered in the graph.
    /// </summary>
    public int Ordinal { get; set; }
}
