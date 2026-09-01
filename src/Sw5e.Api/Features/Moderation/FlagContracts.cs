namespace Sw5e.Api.Features.Moderation;

/// <summary>
/// What a reader sends to report a problem.
/// </summary>
/// <remarks>
/// <para>
/// Four fields, and there is deliberately no <c>targetKind</c> among them.
/// Whether a report is about a picture or about writing follows from the
/// reason, and the server derives it — see <c>ContentFlagRules.KindOf</c>. A
/// field the client supplies is a field the client can contradict, and
/// "picture reason, document target" is a combination that would then have to
/// be rejected somewhere.
/// </para>
/// <para>
/// The strings are nullable because they arrive from a JSON body that anybody
/// can shape however they like. A required <see langword="string"/> property on
/// a minimal-API request record does not make the field arrive; it makes a
/// missing field arrive as null through a non-nullable reference, and the
/// resulting <see cref="NullReferenceException"/> is a 500 where a 400 belongs.
/// </para>
/// </remarks>
/// <param name="Reason">
/// One of the published reason names — <c>image-artist-known</c>,
/// <c>text-error</c> and the rest.
/// </param>
/// <param name="TargetType">
/// A content type key or route segment. For a picture this is
/// <c>asset-credit</c>: every image the site publishes has an attribution
/// record, keyed <c>{group}-{key}</c>, and that record is both what identifies
/// the picture and what a reviewer edits to resolve the report.
/// </param>
/// <param name="TargetKey">The document's slug within its type.</param>
/// <param name="Details">
/// What the reporter wants to say. Optional except for <c>other</c>, capped,
/// and stored exactly as sent — see <c>ContentFlagRow.Details</c> for why it is
/// never sanitised on the way in.
/// </param>
public sealed record RaiseFlagRequest(
    string? Reason,
    string? TargetType,
    string? TargetKey,
    string? Details);

/// <summary>What a reviewer sends to move a flag through the lifecycle.</summary>
/// <param name="Status">The state the flag should end up in.</param>
/// <param name="Note">
/// Why, for the other reviewers. Never shown to the person who raised the
/// report: it is a triage note between the people working the queue, and a
/// field that is sometimes private and sometimes not is a field somebody will
/// eventually write the wrong thing into.
/// </param>
public sealed record UpdateFlagStatusRequest(string? Status, string? Note);

/// <summary>
/// An account named on a flag.
/// </summary>
/// <remarks>
/// <para>
/// The display name and nothing else — no email address, ever. The queue is
/// read by Contributors, who are trusted with content and are not thereby
/// entitled to the address of everybody who has ever reported a typo.
/// </para>
/// <para>
/// <see cref="DisplayName"/> is null when the identifier no longer matches an
/// account. That is a real state rather than an error: a flag outlives the
/// account that raised it, and a queue that dropped those rows would lose the
/// reports of exactly the people who left.
/// </para>
/// </remarks>
public sealed record FlagAccountResponse(Guid Id, string? DisplayName);

/// <summary>One report, as the queue and the reporter's own list show it.</summary>
/// <param name="ReviewerNote">
/// Present for a reviewer and always null on a reporter's own list. See
/// <see cref="UpdateFlagStatusRequest.Note"/>.
/// </param>
public sealed record FlagResponse(
    Guid Id,
    string TargetKind,
    string TargetType,
    string TargetKey,
    string TargetName,
    string Reason,
    string? Details,
    string Status,
    DateTimeOffset CreatedAt,
    FlagAccountResponse Reporter,
    DateTimeOffset? ReviewedAt,
    FlagAccountResponse? ReviewedBy,
    string? ReviewerNote);

/// <summary>A page of reports.</summary>
public sealed record FlagListResponse(
    IReadOnlyList<FlagResponse> Flags,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

/// <summary>
/// The shape of the queue, so it can be read before it is worked through.
/// </summary>
/// <remarks>
/// This exists because of a specific failure the queue would otherwise have on
/// its first day. Around a hundred and fifty of the site's pictures carry no
/// recorded artist, and the moment readers can say so, the queue is a hundred
/// and fifty near-identical attribution reports with one typo correction
/// somewhere in the middle. Paging through them in date order is how the typo
/// is never seen.
/// <para>
/// So the queue is entered through counts rather than through rows: how many
/// are outstanding, how they split by reason, and which handful of documents
/// account for most of them. A reviewer with twenty minutes picks a reason and
/// works it; a reviewer looking for what is urgent sees the rights complaints
/// separated out rather than averaged in.
/// </para>
/// </remarks>
/// <param name="Total">Every report ever raised, in any state.</param>
/// <param name="Outstanding">Open and accepted together — the actual worklist.</param>
/// <param name="ByStatus">One entry per status, including the empty ones.</param>
/// <param name="ByReason">
/// One entry per reason that has at least one outstanding report. Reasons with
/// none are omitted rather than listed at zero: this is a worklist, and ten
/// rows of nothing is nine rows of noise.
/// </param>
/// <param name="MostFlagged">
/// The documents with the most outstanding reports against them, worst first.
/// This is what turns a hundred and fifty rows into one line saying which
/// pictures they are about.
/// </param>
public sealed record FlagSummaryResponse(
    int Total,
    int Outstanding,
    IReadOnlyList<FlagCountResponse> ByStatus,
    IReadOnlyList<FlagCountResponse> ByReason,
    IReadOnlyList<FlagTargetSummaryResponse> MostFlagged);

/// <summary>A count against one named bucket.</summary>
public sealed record FlagCountResponse(string Key, int Count);

/// <summary>One document, with how many outstanding reports it carries.</summary>
public sealed record FlagTargetSummaryResponse(
    string TargetKind,
    string TargetType,
    string TargetKey,
    string TargetName,
    int OutstandingCount);
