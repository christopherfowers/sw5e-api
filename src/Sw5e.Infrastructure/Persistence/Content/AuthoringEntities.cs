namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// One recorded change to a content document, holding the whole document as it
/// stood afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and the database says so.</b> The authoring migration
/// installs a trigger that raises on any <c>UPDATE</c> or <c>DELETE</c> against
/// this table. Application code never attempts either, so the trigger is not
/// there to catch this codebase — it is there because the value of an audit
/// trail is exactly the confidence that it has not been edited, and "we do not
/// write that statement anywhere" is a weaker claim than "the statement is
/// refused". A contributor group small enough to rewrite canonical rules for the
/// whole community is small enough that the record of who did needs to be
/// outside their reach.
/// </para>
/// <para>
/// <b>Deliberately not a foreign key to <see cref="ContentItemRow"/>.</b> The
/// history has to outlive the document. If an item is withdrawn, the record of
/// what it said and who changed it is the thing most worth keeping, and a
/// cascade would delete precisely that. The link is
/// <see cref="ContentType"/> plus <see cref="ItemKey"/>, which is the domain
/// identity anyway, and which keeps resolving if the row is recreated later.
/// The type <em>is</em> constrained, so a revision cannot be filed under a type
/// the API will never ask about.
/// </para>
/// <para>
/// <b>Why the body is stored whole.</b> See <c>IContentAuthoringStore</c> for
/// the full argument. In short: the corpus averages 1.3 kB a document, jsonb
/// over a couple of kilobytes is compressed by PostgreSQL without being asked,
/// and a delta chain would turn a diff into a replay and make one damaged row
/// destroy every version after it.
/// </para>
/// </remarks>
public sealed class ContentRevisionRow
{
    public long Id { get; set; }

    /// <summary>Canonical content type key. Foreign key to the type registry.</summary>
    public required string ContentType { get; set; }

    /// <summary>The document's slug within its type.</summary>
    public required string ItemKey { get; set; }

    /// <summary>
    /// Position in this document's own history, from 1.
    /// </summary>
    /// <remarks>
    /// Per document rather than global so that a person can refer to "revision
    /// 3 of the Wookiee" and mean something. Allocated under the same
    /// transaction that writes the row, and carrying a unique constraint with
    /// the type and key, so two concurrent publishes cannot both take the same
    /// number — one fails and retries rather than silently interleaving.
    /// </remarks>
    public int Number { get; set; }

    /// <summary>Display name at the time, so a history reads without a join.</summary>
    public required string Name { get; set; }

    /// <summary>The complete document after this change, as jsonb.</summary>
    public required string Body { get; set; }

    /// <summary>The document's change token after this change.</summary>
    public required string Version { get; set; }

    /// <summary>What the actor was doing, stored as its wire spelling.</summary>
    public required string Action { get; set; }

    /// <summary>
    /// The account responsible, or null for the deploy-time importer.
    /// </summary>
    /// <remarks>
    /// Not a foreign key. Identity lives in a separate database — deliberately,
    /// so that a content-side mistake cannot reach the passkey tables — and a
    /// constraint across that boundary would either fail to exist or force the
    /// two together. The same soft-reference approach the moderation schema
    /// already takes for its reporters.
    /// </remarks>
    public Guid? ActorUserId { get; set; }

    /// <summary>The actor's own note about why, if they left one.</summary>
    public string? Reason { get; set; }

    /// <summary>Schema version the body was validated against.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// For a revert, the revision whose body was restored.
    /// </summary>
    /// <remarks>
    /// Recorded so that "this is revision 9, and it put back what revision 4
    /// said" is answerable from the row. Without it a revert is
    /// indistinguishable from someone retyping the old text, which is the one
    /// thing a reviewer auditing a reversal needs to be able to tell apart.
    /// </remarks>
    public long? RevertedFromId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A proposed document that is not live: work a contributor has saved and an
/// administrator has not yet published.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why drafts are their own table rather than a status column.</b> Putting a
/// <c>status</c> on <see cref="ContentItemRow"/> would mean every read query —
/// list, get, search, count, and the search SQL's four-stage CTE — grows a
/// predicate, and forgetting it in any one of them publishes unfinished work to
/// the whole community. It would also make editing a published document
/// destructive: the live row would have to hold the half-finished version while
/// somebody worked on it. A separate table means the read path is untouched,
/// literally: the same tables, the same predicates, the same plans it had
/// before authoring existed.
/// </para>
/// <para>
/// <b>One draft per document, not a queue of them.</b> The unique constraint on
/// type and key means two contributors editing the same entry collide
/// immediately and visibly, rather than each building a version that silently
/// discards the other's at publication. With a council of a few people, a
/// collision is a conversation; a lost afternoon is not.
/// </para>
/// </remarks>
public sealed class ContentDraftRow
{
    public long Id { get; set; }

    /// <summary>Canonical content type key. Foreign key to the type registry.</summary>
    public required string ContentType { get; set; }

    /// <summary>
    /// The slug this will publish to. May name a document that does not exist
    /// yet, which is how new content is authored.
    /// </summary>
    public required string ItemKey { get; set; }

    /// <summary>Display name lifted from the draft body, for the worklist.</summary>
    public required string Name { get; set; }

    /// <summary>The proposed document, as jsonb.</summary>
    public required string Body { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid UpdatedByUserId { get; set; }

    /// <summary>
    /// The revision this draft was started from, or null when the document did
    /// not exist.
    /// </summary>
    /// <remarks>
    /// What makes it possible to say "somebody else has published this since
    /// you started" instead of silently overwriting their work. The store
    /// compares it to the document's current revision at publication.
    /// </remarks>
    public long? BaseRevisionId { get; set; }

    /// <summary>
    /// The moderation report this draft answers, when it came from the queue.
    /// </summary>
    /// <remarks>
    /// Carried on the draft rather than only on the flag so that the connection
    /// survives the work: a reviewer accepts a report, starts the fix, and the
    /// draft knows what it is for. At publication the store copies the revision
    /// it wrote back onto the flag, which is what closes the loop the flag
    /// queue opened. Not a foreign key — moderation is its own schema, and may
    /// be its own database.
    /// </remarks>
    public Guid? ResolvesFlagId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
