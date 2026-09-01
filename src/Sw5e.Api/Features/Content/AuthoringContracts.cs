using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// The body of a draft save: the whole proposed document, plus an optional link
/// to the report it answers.
/// </summary>
/// <param name="Document">
/// The complete content document, exactly as it must validate against its JSON
/// Schema. Sent whole rather than as a patch because the schema describes whole
/// documents: a partial body cannot be validated against one, so a patch would
/// have to be applied first and validated after, which means a moment where the
/// stored document is neither the old one nor a checked one.
/// </param>
/// <param name="ResolvesFlagId">
/// The moderation report this work answers, when it came out of the queue.
/// Optional, and only ever set — a save that omits it leaves an existing link
/// alone rather than quietly detaching the draft from its report.
/// </param>
public sealed record SaveDraftRequest(
    [property: JsonPropertyName("document")] JsonElement Document,
    [property: JsonPropertyName("resolvesFlagId")] Guid? ResolvesFlagId);

/// <summary>The body of a publish or a revert: why.</summary>
/// <param name="Reason">
/// The actor's note. Optional, bounded, stored verbatim and escaped on output,
/// exactly like a reviewer's note on a flag.
/// </param>
public sealed record AuthoringReasonRequest(
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>The body of a revert: which revision to restore, and why.</summary>
public sealed record RevertRequest(
    [property: JsonPropertyName("revisionId")] long RevisionId,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>One entry in the authoring worklist.</summary>
public sealed record DraftSummaryResponse(
    string Type,
    string Key,
    string Name,
    bool TargetExists,
    bool BaseRevisionIsCurrent,
    Guid CreatedByUserId,
    Guid UpdatedByUserId,
    Guid? ResolvesFlagId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The worklist.</summary>
public sealed record DraftListResponse(IReadOnlyList<DraftSummaryResponse> Drafts);

/// <summary>One draft in full.</summary>
public sealed record DraftResponse(
    string Type,
    string Key,
    JsonElement Document,
    Guid CreatedByUserId,
    Guid UpdatedByUserId,
    long? BaseRevisionId,
    Guid? ResolvesFlagId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One entry in a document's history, without its body.</summary>
public sealed record RevisionSummaryResponse(
    long Id,
    string Type,
    string Key,
    int Number,
    string Action,
    Guid? ActorUserId,
    string? Reason,
    long? RevertedFromId,
    DateTimeOffset CreatedAt);

/// <summary>A document's history.</summary>
public sealed record RevisionListResponse(IReadOnlyList<RevisionSummaryResponse> Revisions);

/// <summary>
/// One revision in full, including the document as it stood after that change.
/// </summary>
/// <remarks>
/// This is what a diff is built from: fetch two, compare the documents. The API
/// deliberately does not compute the diff. Rendering a change to a nested
/// document is a presentation decision — which fields matter, how prose is
/// segmented, what counts as a move rather than a delete and an insert — and
/// baking one answer into the response would fix it for every client forever.
/// </remarks>
public sealed record RevisionResponse(
    long Id,
    string Type,
    string Key,
    int Number,
    string Action,
    Guid? ActorUserId,
    string? Reason,
    int SchemaVersion,
    long? RevertedFromId,
    DateTimeOffset CreatedAt,
    JsonElement Document);
