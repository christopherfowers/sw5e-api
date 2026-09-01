using System.Text.Json;

namespace Sw5e.Domain.Content;

/// <summary>
/// The write side of the content store: drafts, publication, history and
/// revert.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not part of <see cref="IContentRepository"/>.</b> That
/// contract is the read path the whole community depends on, it has two
/// implementations that are held to parity, and one of them reads a read-only
/// volume it could not write to if it tried. Widening it would oblige the
/// file-backed store to grow methods that can only throw. Authoring is a
/// separate capability with a separate implementation, and a deployment that
/// has not enabled it simply does not register this service.
/// </para>
/// <para>
/// <b>Why a revision is a full snapshot and not a delta.</b> The corpus is
/// 7,877 documents totalling about ten megabytes — a mean of roughly 1.3 kB,
/// with the largest single document at 466 kB. Storing the whole document on
/// every change is therefore cheap in absolute terms, and PostgreSQL compresses
/// any jsonb value over a couple of kilobytes out to TOAST storage without
/// being asked, so the few large documents are the ones that compress best.
/// </para>
/// <para>
/// A delta chain would buy a fraction of that space and charge for it three
/// times. Reconstructing an old version becomes a replay of every delta since
/// the last keyframe, so a diff against revision 2 of a long-lived document is
/// O(n) reads instead of two. It needs a patch format, a merge implementation
/// and keyframes anyway, which is two mechanisms where there was one. And it is
/// fragile in the way that matters most here: a single corrupted or missing
/// delta destroys every version after it, whereas a damaged snapshot costs
/// exactly one revision. The point of this table is to be the record that can
/// be trusted when something has gone wrong with the corpus, so it is built to
/// fail in isolation.
/// </para>
/// <para>
/// The snapshot is of the document <em>after</em> the change. That makes a diff
/// two row reads, a revert one read and one write, and "what does the history
/// say the document was on the 3rd" answerable without walking anything.
/// </para>
/// <para>
/// <b>Append-only.</b> Nothing here updates or deletes a revision, and the
/// database refuses to as well — see the rule installed by the authoring
/// migration. A revert writes a <em>new</em> revision carrying the old body
/// rather than removing the revisions in between, so the fact that a change was
/// made and undone survives the undoing. A small group of contributors can
/// rewrite canonical rules for the whole community; the audit trail of that has
/// to be something none of them can quietly edit.
/// </para>
/// </remarks>
public interface IContentAuthoringStore
{
    /// <summary>The draft for one document, or null when there is none.</summary>
    Task<ContentDraft?> GetDraftAsync(
        ContentTypeDefinition type,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every outstanding draft, most recently touched first. This is the
    /// authoring worklist.
    /// </summary>
    Task<IReadOnlyList<ContentDraftSummary>> ListDraftsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces the draft for one document.
    /// </summary>
    /// <remarks>
    /// The body is validated here, not only at publication. A draft that cannot
    /// be published is a trap: the contributor believes the work is saved, and
    /// discovers at the last step that it never conformed. Refusing on the way
    /// in costs one round trip and reports the failing field while the author is
    /// still looking at it.
    /// </remarks>
    Task<ContentAuthoringResult> SaveDraftAsync(
        ContentTypeDefinition type,
        string key,
        JsonElement body,
        Guid actorUserId,
        Guid? resolvesFlagId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws the draft away. Returns false when there was none.</summary>
    Task<bool> DiscardDraftAsync(
        ContentTypeDefinition type,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a draft live: validates it again, writes it to the catalogue,
    /// records a revision, and clears the draft — all in one transaction.
    /// </summary>
    /// <remarks>
    /// Revalidated at publication even though <see cref="SaveDraftAsync"/>
    /// already checked, because the schema may have moved since the draft was
    /// written. The check that decides what enters the corpus has to be the one
    /// that runs at the moment it enters.
    /// </remarks>
    Task<ContentAuthoringResult> PublishDraftAsync(
        ContentTypeDefinition type,
        string key,
        Guid actorUserId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>One document's history, newest first, without bodies.</summary>
    Task<IReadOnlyList<ContentRevisionSummary>> ListRevisionsAsync(
        ContentTypeDefinition type,
        string key,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One revision in full, including its body, so two of them can be diffed.
    /// </summary>
    Task<ContentRevision?> GetRevisionAsync(
        ContentTypeDefinition type,
        string key,
        long revisionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the body of an earlier revision as a new revision.
    /// </summary>
    /// <remarks>
    /// Validated like any other write. A revision recorded under an older schema
    /// may no longer conform, and restoring it blindly would put a document into
    /// the corpus that the corpus's own rules reject — which is exactly the
    /// silent degradation this whole path exists to prevent. Such a revert is
    /// refused with the schema errors, and the fix is to author forward.
    /// </remarks>
    Task<ContentAuthoringResult> RevertAsync(
        ContentTypeDefinition type,
        string key,
        long revisionId,
        Guid actorUserId,
        string? reason,
        CancellationToken cancellationToken = default);
}
