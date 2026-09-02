using System.Text.Json;

namespace Sw5e.Domain.Content;

/// <summary>
/// Why a revision exists — what the actor was doing when it was written.
/// </summary>
/// <remarks>
/// Recorded rather than inferred from position in the history. "The first
/// revision" and "an import" are not the same claim: the corpus was imported
/// once already, and a document created by a contributor afterwards also has a
/// first revision. A reviewer reading the history needs to know which they are
/// looking at without counting rows.
/// </remarks>
public enum ContentRevisionAction
{
    /// <summary>Written by the deploy-time importer, not by a person.</summary>
    Imported,

    /// <summary>A document that did not exist before was published.</summary>
    Created,

    /// <summary>An existing document was replaced.</summary>
    Updated,

    /// <summary>An earlier revision's body was restored.</summary>
    Reverted,
}

/// <summary>
/// One point in a document's history: the whole document as it stood after the
/// change, and who made it.
/// </summary>
/// <param name="Id">Surrogate key; what a resolved flag points at.</param>
/// <param name="ContentType">Canonical content type key.</param>
/// <param name="ItemKey">The document's slug within its type.</param>
/// <param name="Number">
/// Position in this document's own history, starting at 1. Per document rather
/// than global, so "revision 3 of the Wookiee" is a thing a person can say.
/// </param>
/// <param name="Body">
/// The complete document as it stood after this change. See
/// <see cref="IContentAuthoringStore"/> for why this is a snapshot and not a
/// delta.
/// </param>
/// <param name="Action">What the actor was doing.</param>
/// <param name="ActorUserId">
/// The account that made the change, or null for the importer. Not a foreign
/// key: identity is a separate database, and this schema must not be able to
/// reach into it.
/// </param>
/// <param name="Reason">The actor's own note, if they left one.</param>
/// <param name="SchemaVersion">The schema version the body was validated against.</param>
/// <param name="RevertedFromId">
/// When <paramref name="Action"/> is <see cref="ContentRevisionAction.Reverted"/>,
/// the revision whose body was restored. Null otherwise.
/// </param>
/// <param name="CreatedAt">When the change was made.</param>
public sealed record ContentRevision(
    long Id,
    string ContentType,
    string ItemKey,
    int Number,
    JsonElement Body,
    ContentRevisionAction Action,
    Guid? ActorUserId,
    string? Reason,
    int SchemaVersion,
    long? RevertedFromId,
    DateTimeOffset CreatedAt);

/// <summary>
/// A revision without its body, for listing a history without shipping every
/// version of a four-hundred-kilobyte document to draw a list.
/// </summary>
public sealed record ContentRevisionSummary(
    long Id,
    string ContentType,
    string ItemKey,
    int Number,
    ContentRevisionAction Action,
    Guid? ActorUserId,
    string? Reason,
    long? RevertedFromId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Work in progress: a proposed document that is not live and is not visible to
/// readers.
/// </summary>
/// <remarks>
/// A draft is a row in its own table rather than a status column on the live
/// document. That is the difference between a council member being able to
/// work on the Wookiee entry and the Wookiee entry disappearing from the site
/// while they do. It also means the read path — which serves the whole
/// community and is the thing that must not regress — is not touched by any of
/// this: it queries the same table, with the same predicates, as it did before.
/// </remarks>
/// <param name="ContentType">Canonical content type key.</param>
/// <param name="ItemKey">
/// The slug the draft will publish to. A draft may name a document that does
/// not exist yet, which is how new content is authored.
/// </param>
/// <param name="Body">The proposed document.</param>
/// <param name="CreatedByUserId">Who started it.</param>
/// <param name="UpdatedByUserId">Who touched it last.</param>
/// <param name="BaseRevisionId">
/// The revision this draft was started from, or null when the document did not
/// exist. What makes it possible to tell a draft that is still current from one
/// written against a version somebody else has since replaced.
/// </param>
/// <param name="ResolvesFlagId">
/// The moderation report this draft is being written to answer, if it came out
/// of the review queue.
/// </param>
public sealed record ContentDraft(
    string ContentType,
    string ItemKey,
    JsonElement Body,
    Guid CreatedByUserId,
    Guid UpdatedByUserId,
    long? BaseRevisionId,
    Guid? ResolvesFlagId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A draft without its body, for the queue listing.</summary>
public sealed record ContentDraftSummary(
    string ContentType,
    string ItemKey,
    string Name,
    bool TargetExists,
    bool BaseRevisionIsCurrent,
    Guid CreatedByUserId,
    Guid UpdatedByUserId,
    Guid? ResolvesFlagId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Why an authoring operation did not do what was asked.</summary>
public enum ContentAuthoringStatus
{
    /// <summary>It worked.</summary>
    Succeeded,

    /// <summary>The document failed its schema.</summary>
    Invalid,

    /// <summary>There is no draft, document or revision by that name.</summary>
    NotFound,

    /// <summary>
    /// The document moved underneath the draft: it has been published since the
    /// draft was started.
    /// </summary>
    Stale,
}

/// <summary>The result of an operation that writes.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Errors">
/// The schema failures, when <paramref name="Status"/> is
/// <see cref="ContentAuthoringStatus.Invalid"/>.
/// </param>
/// <param name="Revision">
/// The revision the operation wrote, when it wrote one. This is what a resolved
/// flag is pointed at.
/// </param>
/// <param name="Violations">
/// The same failures with the location, keyword and message kept apart, so the
/// editor can put each one beside the control that caused it. Empty for a
/// refusal that is not about a value inside the document.
/// </param>
public sealed record ContentAuthoringResult(
    ContentAuthoringStatus Status,
    IReadOnlyList<string> Errors,
    ContentRevisionSummary? Revision,
    IReadOnlyList<ContentViolation> Violations)
{
    public static ContentAuthoringResult Succeeded(ContentRevisionSummary? revision = null) =>
        new(ContentAuthoringStatus.Succeeded, [], revision, []);

    /// <summary>
    /// A refusal with reasons but nothing to place them by — a body that would
    /// not parse, or a type with no schema published.
    /// </summary>
    public static ContentAuthoringResult Invalid(IReadOnlyList<string> errors) =>
        new(ContentAuthoringStatus.Invalid, errors, null, []);

    /// <summary>A refusal that knows which value each reason was about.</summary>
    public static ContentAuthoringResult Invalid(ContentValidation validation) =>
        new(
            ContentAuthoringStatus.Invalid,
            validation.Errors,
            null,
            validation.Violations);

    public static ContentAuthoringResult NotFound { get; } =
        new(ContentAuthoringStatus.NotFound, [], null, []);

    public static ContentAuthoringResult Stale { get; } =
        new(ContentAuthoringStatus.Stale, [], null, []);
}
