using NpgsqlTypes;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// One row of the content type registry, mirrored into the database.
/// </summary>
/// <remarks>
/// <para>
/// The authoritative registry is <see cref="Domain.Content.ContentTypeRegistry"/>,
/// a compile-time constant list. This table is seeded from it by a migration and
/// exists for one reason: so <c>content_item.content_type</c> can carry a
/// foreign key. Without it the type column is free text, and a bad importer or
/// a hand-run <c>UPDATE</c> can put a row in the table under a type the API
/// will never look for — a row that exists, counts toward nothing, and is
/// invisible until someone runs a manual query and finds it.
/// </para>
/// <para>
/// The display labels are carried too, so the schema documents itself when read
/// with <c>psql</c>. The API does not read them: it answers the registry
/// endpoint from the compiled list, because a label the code and the database
/// disagree about should resolve in favour of the code that renders it.
/// </para>
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
/// One content document: its identity, the projected columns every list and
/// search query filters and orders on, and the document itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one table with a jsonb body rather than a table per content type.</b>
/// </para>
/// <para>
/// The fourteen SW5e content types have almost nothing in common below the
/// surface.
/// A species carries <c>traits[]</c>, <c>abilityScoreIncreaseOptions[]</c> and a
/// block of markdown lore; a monster carries a stat block with nested action,
/// legendary action and spellcasting structures; equipment carries a different
/// set of fields depending on whether it is a weapon, armour or a consumable.
/// Shredding all of that into third-normal-form tables is perhaps forty tables,
/// and it buys nothing the API asks for — no endpoint queries "every species
/// whose third trait mentions climbing".
/// </para>
/// <para>
/// It also costs something specific and expensive. The published contract for
/// <c>GET /api/content/{type}/{key}</c> is that the response body <em>is</em>
/// the type's JSON Schema, passed through unaltered. A shredded model has to
/// reassemble that document on the way out, which means the schema is now
/// written down twice — once as the published JSON Schema and once as the
/// entity model — with no mechanism that keeps them equal. Every schema
/// revision becomes a migration plus a mapping change, and a field added in the
/// content repository silently disappears from the API until someone notices.
/// Storing the document as jsonb makes the JSON Schema the single definition of
/// what a content item is, which is what it was always meant to be.
/// </para>
/// <para>
/// <b>What jsonb alone would cost, and how that is paid for here.</b> A pure
/// document store cannot cheaply answer "page 3 of the powers, ordered by name,
/// filtered to the core set", cannot count without scanning, and cannot join
/// one item to another. So the columns a query touches are lifted out of the
/// document into real, indexed columns — <see cref="Name"/>,
/// <see cref="SourceKey"/>, <see cref="ContentSet"/>, <see cref="NameLower"/>,
/// <see cref="SearchTextLower"/> — and cross-document links are lifted into
/// <see cref="ContentReferenceRow"/>. Those are a <em>projection</em>, not a
/// second copy of the truth: they are derived from <see cref="Body"/> on every
/// write by the same projection code the file-backed store uses, so they cannot
/// drift from it. Re-running the importer rebuilds them.
/// </para>
/// <para>
/// This is the shape the rest of the codebase was already written against.
/// <c>ContentProjection</c> describes its per-type field lists as "the
/// filesystem store's stand-in for the projected columns a database table would
/// carry"; these are those columns.
/// </para>
/// </remarks>
public sealed class ContentItemRow
{
    /// <summary>
    /// Surrogate key. The domain identity is <see cref="ContentType"/> plus
    /// <see cref="ItemKey"/>, which carries a unique constraint; this exists so
    /// the reference table has something narrow and stable to point at, and so
    /// renaming a slug is one update rather than a cascade.
    /// </summary>
    public long Id { get; set; }

    /// <summary>Canonical content type key. Foreign key to the type registry.</summary>
    public required string ContentType { get; set; }

    /// <summary>
    /// The item's slug, unique within its type. Named <c>ItemKey</c> rather
    /// than <c>Key</c> because <c>key</c> is easy to confuse with the primary
    /// key when reading SQL, and because the column sits beside a real one.
    /// </summary>
    public required string ItemKey { get; set; }

    /// <summary>Display name, lifted from the document's <c>name</c> or <c>title</c>.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Publication the item came from, or null on types that record none.
    /// </summary>
    /// <remarks>
    /// Deliberately not a foreign key to the source item, and not stored only
    /// as a reference row. It is a filter column on the hot list query, so it
    /// belongs where the predicate can reach it without a join. The same value
    /// is <em>also</em> written as a reference row, which is what makes
    /// "everything published in the Player's Handbook" answerable across all
    /// fourteen types at once.
    /// </remarks>
    public string? SourceKey { get; set; }

    /// <summary>Core versus expanded content, or null on types that record neither.</summary>
    public string? ContentSet { get; set; }

    /// <summary>One-line plain-text description for a list row, already truncated.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// The type-specific display fields a list row needs, as a jsonb object of
    /// string values.
    /// </summary>
    /// <remarks>
    /// jsonb rather than a child table because the set of fields differs per
    /// type and is presentation, not data: a row needs a power's level and a
    /// monster's challenge rating rendered as strings, and nothing ever filters
    /// or sorts on them. A child table would turn every list page into a second
    /// query or a join fan-out for values that are only ever displayed.
    /// </remarks>
    public required string Facets { get; set; }

    /// <summary>
    /// The document, exactly as it validates against its published JSON Schema.
    /// </summary>
    /// <remarks>
    /// jsonb rather than json: json keeps the original text byte for byte but
    /// is opaque to the operators and indexes that make this a database rather
    /// than a filing cabinet. The cost is that jsonb normalises — object member
    /// order is not preserved and duplicate members collapse to the last one.
    /// Neither is significant in JSON (RFC 8259 defines objects as unordered),
    /// no consumer of this API depends on member order, and collapsing
    /// duplicates fixes a document that was malformed anyway.
    /// </remarks>
    public required string Body { get; set; }

    /// <summary>
    /// Every piece of prose in the document, flattened for free-text matching.
    /// </summary>
    /// <remarks>
    /// Materialised rather than recomputed from <see cref="Body"/> per query.
    /// Extracting and flattening prose out of jsonb at query time is a function
    /// call per row per request, over a value that changes only at import.
    /// </remarks>
    public required string SearchText { get; set; }

    /// <summary>
    /// Opaque token that changes when <see cref="Body"/> changes, used as the
    /// ETag validator.
    /// </summary>
    /// <remarks>
    /// A content hash rather than a timestamp or a row version: re-importing an
    /// unchanged corpus must not invalidate every client's cache, and a
    /// redeploy that rewrites identical rows is exactly that.
    /// </remarks>
    public required string Version { get; set; }

    /// <summary>
    /// <see cref="Name"/> lowercased, for the case-insensitive name filter.
    /// </summary>
    /// <remarks>
    /// A stored column rather than <c>lower(name)</c> in the predicate, and
    /// rather than <c>ILIKE</c>. Both of those work, but both make the query
    /// depend on the database's collation and case-folding rules, which differ
    /// from .NET's. The file-backed store folds with
    /// <c>ToLowerInvariant</c>; folding once at import with the same call and
    /// comparing the results as plain bytes is what makes the two stores agree
    /// on which rows match, rather than agreeing only for ASCII.
    /// </remarks>
    public required string NameLower { get; set; }

    /// <summary><see cref="SearchText"/> lowercased, for the same reason.</summary>
    public required string SearchTextLower { get; set; }

    /// <summary>
    /// Just the headings in the document's prose, lowercased, one per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="SearchTextLower"/> so that search can score a
    /// heading above a sentence. Without it every free-text match sat in one
    /// tier and the results came back ordered by nothing more meaningful than
    /// the alphabet within whichever content type happened to have the most
    /// hits — "difficult terrain" returned twenty-nine class features before
    /// the rules chapter that has a section named after the phrase.
    /// </para>
    /// <para>
    /// Only the lowercased form is stored. The other columns keep a
    /// cased copy because it is what a snippet is cut from; a heading match
    /// reports the heading itself as its evidence, and the reader is shown the
    /// document's own name for the section rather than a window into prose.
    /// </para>
    /// </remarks>
    public required string HeadingTextLower { get; set; }

    /// <summary>
    /// The document as PostgreSQL full text search sees it, weighted by where
    /// each word was found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The substring ladder above this column answers "does the phrase appear",
    /// which is the right question for a name and the wrong one for prose. It
    /// has no way to say that a chapter about difficult terrain is more about
    /// it than a class feature that mentions it once, so every prose match
    /// scored the same and ordering inside a type fell back to the alphabet.
    /// Searching the deployed site for "difficult terrain" returned the
    /// Adventuring chapter behind twenty-nine class features, in a list headed
    /// by an armoured assault tank, because "AAT" sorts first.
    /// </para>
    /// <para>
    /// Weights are the three fields a reader would rank by: <c>A</c> the name,
    /// <c>B</c> a section heading, <c>D</c> the body. It is a stored generated
    /// column rather than something the importer computes, so it cannot drift
    /// from the text it summarises and no write path can forget to update it.
    /// </para>
    /// <para>
    /// This does not replace the ladder. Full text search is word-based, so it
    /// cannot find "Acrobat" from "acro" and does not connect "blast" to
    /// "blaster" — which in this corpus is most of the weapons. The trigram
    /// indexes stay exactly as they are and keep owning names and substrings;
    /// this column orders the prose beneath them.
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
/// Both kinds exist because the corpus uses both, and pretending otherwise
/// would mean silently dropping most of the graph. Exactly one field in the
/// whole of SW5e content points at another item by slug: <c>sourceKey</c>.
/// Every other cross-reference — a feature's <c>grantedByName</c>, a
/// background's <c>featOptions[].name</c>, a power's <c>prerequisite</c> —
/// names its target by display name, because the documents were transcribed
/// from print, where a name is the only identifier there is.
/// </remarks>
public enum ContentReferenceTargetKind
{
    /// <summary>The target is named by its slug, matched against <c>item_key</c>.</summary>
    Key,

    /// <summary>
    /// The target is named by its display name, matched against <c>name</c>.
    /// </summary>
    /// <remarks>
    /// Names are not unique across the corpus — the feature schema says so
    /// outright — so a name match can be ambiguous. The importer resolves a
    /// name reference only when exactly one candidate of the target type
    /// matches, and leaves it unresolved otherwise rather than picking one
    /// arbitrarily and inventing an edge that is probably wrong.
    /// </remarks>
    Name,
}

/// <summary>
/// One directed link from a content item to another piece of content, resolved
/// where possible and recorded as intent where not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this table exists.</b> Cross-content linkability is the point of
/// putting the catalogue in a database at all. The eventual goal is generating
/// print-ready documents from arbitrary collections — "the Wookiee species and
/// everything it grants", "every feat a background offers", "everything
/// published in the Player's Handbook" — and each of those is a graph
/// traversal. Answering them from documents alone means fetching an item,
/// parsing it, reading an identifier out of it, fetching that, and repeating:
/// one round trip per edge, with every type's link fields hard-coded into
/// whatever is doing the walking. Lifting the edges into rows turns the same
/// question into a join, or a recursive CTE when the walk is unbounded.
/// </para>
/// <para>
/// <b>Why an unresolved edge is a row and not an error.</b> Three of the target
/// types the corpus refers to do not exist as content types at all: an
/// archetype's <c>className</c> points at a class, and equipment properties
/// point at weapon and armour property definitions, none of which have been
/// authored yet. Several references point at items that simply have not been
/// written — six of the eight power prerequisites in the seed corpus name a
/// power that is not in it. Refusing to import any of that would mean the
/// database can only hold a finished corpus, which is the one state it will
/// never be in. So <see cref="TargetType"/> and <see cref="TargetIdentifier"/>
/// record what the document said, and <see cref="ResolvedItemId"/> is filled in
/// only when the target is actually there. An unresolved edge is queryable —
/// which is how "what is this corpus still missing" stops being a grep and
/// becomes a report.
/// </para>
/// <para>
/// <b>Why the resolved target is a real foreign key and the intent is not.</b>
/// Traversal joins run against <see cref="ResolvedItemId"/>, so they are index
/// lookups on a narrow integer and the database guarantees they point at a row
/// that exists. <see cref="TargetType"/> and <see cref="TargetIdentifier"/>
/// carry no constraint beyond a format check, because constraining them is
/// exactly the thing that would make an in-progress corpus unimportable.
/// </para>
/// <para>
/// <b>Why edges are typed and positional.</b> <see cref="Relation"/> says what
/// kind of link it is, so "the book this came from" and "the feat this
/// requires" are distinguishable without re-parsing the document.
/// <see cref="JsonPath"/> records where the link was found, so an unresolved
/// reference can be reported precisely enough for someone to go and fix it.
/// <see cref="Ordinal"/> keeps an ordered list in the document ordered in the
/// graph, which matters when the list is a background's feat options and the
/// order is the roll order.
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
    /// <c>$.featOptions[3].name</c>. Unique per item and relation, which is
    /// what makes re-importing an item idempotent for its edges too.
    /// </summary>
    public required string JsonPath { get; set; }

    /// <summary>
    /// Type of content the link points at. Usually a registered content type,
    /// but deliberately not constrained to one: <c>class</c> is referenced by
    /// every archetype and does not exist yet.
    /// </summary>
    public required string TargetType { get; set; }

    /// <summary>Whether <see cref="TargetIdentifier"/> is a slug or a display name.</summary>
    public required ContentReferenceTargetKind TargetKind { get; set; }

    /// <summary>
    /// The slug or display name the document gave, verbatim after trimming.
    /// Kept even when the target resolves, so the edge can be re-resolved after
    /// the missing content is authored without re-reading every document.
    /// </summary>
    public required string TargetIdentifier { get; set; }

    /// <summary>
    /// The item this edge actually reaches, or null when nothing matches or
    /// when a name match was ambiguous.
    /// </summary>
    public long? ResolvedItemId { get; set; }

    /// <summary>Navigation to the resolved target.</summary>
    public ContentItemRow? ResolvedItem { get; set; }

    /// <summary>
    /// Position among the links of the same relation on the same item, so an
    /// ordered list in the document stays ordered in the graph.
    /// </summary>
    public int Ordinal { get; set; }
}
