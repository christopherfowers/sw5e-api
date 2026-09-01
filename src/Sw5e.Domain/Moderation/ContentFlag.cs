namespace Sw5e.Domain.Moderation;

/// <summary>
/// What somebody is reporting, and about what.
/// </summary>
/// <remarks>
/// <para>
/// The taxonomy is closed and it is a <see langword="enum"/> rather than a
/// string, because a reason is not a label — it is a routing decision. Every
/// value below sends the report to a different person and asks them for
/// different work, and a free-text "category" field would collapse that back
/// into a pile somebody has to read end to end. The test for whether a reason
/// deserves to exist is therefore not "is this a distinct kind of wrongness"
/// but "does a reviewer do something different about it".
/// </para>
/// <para>
/// The values divide into two sets by what they can be raised against — see
/// <see cref="FlagTargetKind"/> — because "the artist is wrong" is not a
/// statement anybody can make about a paragraph of rules text, and offering it
/// there produces reports nobody can act on.
/// </para>
/// </remarks>
public enum FlagReason
{
    /* ------------------------------------------------------------ pictures */

    /// <summary>
    /// "I know who made this."
    /// </summary>
    /// <remarks>
    /// The reason this whole feature was built first. The archive carries
    /// roughly a hundred and fifty pictures inherited from the original
    /// sw5e.com whose artist was never recorded, and that knowledge exists —
    /// scattered across people who recognise a style, remember a commission, or
    /// made the picture themselves. Until now it had nowhere to go, and every
    /// month that passed lost more of it.
    /// <para>
    /// This is the one reason whose report is worth more than the flag: what a
    /// reviewer does with it is edit an <c>asset-credit</c> document from
    /// <c>inherited-unattributed</c> to <c>cited</c>, and the free text is
    /// where the evidence for that lives.
    /// </para>
    /// </remarks>
    ImageArtistKnown,

    /// <summary>
    /// The credit on this picture is missing, incomplete or wrong, and the
    /// reporter does not know what it should say.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ImageArtistKnown"/> because the work is
    /// different: this one starts a search, that one ends one. Merging them
    /// would bury every usable identification among reports that only say
    /// "somebody should look into this".
    /// </remarks>
    ImageAttributionMissing,

    /// <summary>
    /// This picture should be replaced with work the project owns.
    /// </summary>
    /// <remarks>
    /// The owner's own words for why this feature exists. It is an editorial
    /// and commissioning queue rather than a correction: nothing about the page
    /// is factually wrong, and the fix is somebody drawing a replacement.
    /// </remarks>
    ImageReplacementWanted,

    /// <summary>
    /// The rights holder objects to this picture being published here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not in the list this was asked for, and added anyway. It is the same
    /// sentence as <see cref="ImageReplacementWanted"/> read from the other
    /// side, and it behaves nothing like it: one is a wish, the other is a
    /// person saying their work is being used without permission. That has a
    /// clock on it and an obligation attached, and it must not queue behind
    /// two hundred "would be nice to redraw" reports.
    /// </para>
    /// <para>
    /// The queue sorts on this ahead of everything else for that reason. The
    /// project inherited an art pool it does not have paperwork for; the
    /// realistic way that becomes a problem is one artist finding one picture,
    /// and the difference between handling that well and handling it badly is
    /// whether their report was seen the same day.
    /// </para>
    /// </remarks>
    ImageRightsComplaint,

    /// <summary>The picture does not show what the page is about.</summary>
    /// <remarks>
    /// A mis-keyed asset rather than a rights or attribution problem: the
    /// picture is fine and is on the wrong page. Cheap to fix and cheap to
    /// verify, which is exactly why it should not sit in the same bucket as the
    /// two above.
    /// </remarks>
    ImageWrongSubject,

    /* ------------------------------------------------------------- writing */

    /// <summary>A typo, a broken link, or mangled formatting.</summary>
    /// <remarks>
    /// The cheapest report to act on and the most common one a reader will
    /// have. It is worth its own value purely so it can be filtered <em>out</em>
    /// of a session spent on attribution, and filtered <em>to</em> by somebody
    /// with ten minutes.
    /// </remarks>
    TextError,

    /// <summary>The rules text disagrees with the book it came from.</summary>
    /// <remarks>
    /// Needs somebody holding the source to settle it, which makes it a
    /// different queue from <see cref="TextError"/> even though both are "the
    /// words are wrong".
    /// </remarks>
    ContentIncorrect,

    /// <summary>Something that should be here is absent.</summary>
    /// <remarks>
    /// A missing feature, a truncated table, a class level with nothing under
    /// it. Split from <see cref="ContentIncorrect"/> because the answer is
    /// authoring rather than correcting, and because these are the reports that
    /// say most about which part of the import dropped something.
    /// </remarks>
    ContentMissing,

    /// <summary>This page cites the wrong source, or cites none.</summary>
    /// <remarks>
    /// The text equivalent of <see cref="ImageAttributionMissing"/>, and
    /// deliberately not folded into <see cref="ContentIncorrect"/>: the rule
    /// may be perfectly right and still be attributed to the wrong book, which
    /// is a citation fix rather than a rules fix and goes to whoever owns the
    /// source records.
    /// </remarks>
    SourceAttribution,

    /* --------------------------------------------------------------- other */

    /// <summary>Something the list above does not cover.</summary>
    /// <remarks>
    /// Present on both target kinds, and the only reason for which free text is
    /// mandatory — a report that says nothing but "other" is a report nobody
    /// can act on, so it is refused at the endpoint rather than accepted and
    /// left to rot in the queue.
    /// <para>
    /// It is also the taxonomy's own feedback channel. A run of "other" reports
    /// that all say the same thing is the evidence for adding a tenth value.
    /// </para>
    /// </remarks>
    Other,
}

/// <summary>What a flag can be raised against.</summary>
/// <remarks>
/// <para>
/// Both kinds resolve to a content document, and that is a deliberate
/// simplification rather than a coincidence. Every picture the site publishes
/// already has an <c>asset-credit</c> document recording what is known about
/// its provenance, keyed <c>{group}-{key}</c> — <c>species-wookiee</c>,
/// <c>classes-guardian</c>, <c>brand-logo</c>. So an image flag points at that
/// record, which means it points at the very document a reviewer has to edit
/// to resolve it, and it means one existence check covers both kinds.
/// </para>
/// <para>
/// The kind is therefore derived from the reason rather than sent by the
/// client. A field the caller supplies is a field the caller can get wrong, and
/// "image reason, document target" is a combination with no meaning that
/// something would then have to reject.
/// </para>
/// </remarks>
public enum FlagTargetKind
{
    /// <summary>A content document: a species, a power, a rules chapter.</summary>
    Document,

    /// <summary>A picture, addressed through its <c>asset-credit</c> record.</summary>
    Image,
}

/// <summary>Where a report has got to.</summary>
/// <remarks>
/// <para>
/// Four states, and the shape of them matters more than the count. The obvious
/// design is three — open, done, rejected — and it is wrong for this queue,
/// because the single most valuable thing a reviewer can record is "yes, this
/// is real, and it is not fixed yet". That is <see cref="Accepted"/>: it is the
/// worklist. Without it, agreeing with a report and fixing it are the same
/// button, so a reviewer who reads two hundred attribution reports in an
/// evening either fixes all two hundred or leaves the queue exactly as they
/// found it.
/// </para>
/// <para>
/// <see cref="Open"/> and <see cref="Accepted"/> are both outstanding.
/// <see cref="Declined"/> and <see cref="Resolved"/> are both finished, and are
/// two states rather than one because "we fixed it" and "there was nothing to
/// fix" are different answers to the person who reported it.
/// </para>
/// </remarks>
public enum FlagStatus
{
    /// <summary>Raised. Nobody has looked at it yet.</summary>
    Open,

    /// <summary>A reviewer agrees there is something here. Still outstanding.</summary>
    Accepted,

    /// <summary>A reviewer judged there is nothing to do. Finished.</summary>
    Declined,

    /// <summary>The thing reported has been put right. Finished.</summary>
    Resolved,
}

/// <summary>
/// The rules about which reason may be raised against what, and which status
/// may follow which.
/// </summary>
/// <remarks>
/// Both live in the domain rather than in the endpoint so that the store, the
/// endpoint and the tests are reasoning from one table. A transition table
/// duplicated into a handler is a transition table that will disagree with
/// itself the first time somebody adds a state.
/// </remarks>
public static class ContentFlagRules
{
    /// <summary>
    /// The content type an image flag must point at. Everything the site draws
    /// has a record here; a picture with no <c>asset-credit</c> document is a
    /// picture the site does not publish.
    /// </summary>
    public const string ImageContentType = "asset-credit";

    /// <summary>Longest free-text explanation accepted, in characters.</summary>
    /// <remarks>
    /// <para>
    /// A thousand is generous for the job — the useful ones are a sentence
    /// naming an artist and a place to verify it — and it is a bound rather
    /// than a style guide. This column is written from a request an
    /// authenticated but otherwise unprivileged account controls, it is read
    /// back by moderators, and an unbounded text field reachable that way is a
    /// storage cost and a rendering cost somebody else pays.
    /// </para>
    /// <para>
    /// Measured after trimming and in .NET characters, which is what the column
    /// length constrains. That is not the same as user-perceived characters for
    /// text outside the basic plane, and the difference does not matter here:
    /// the limit exists to bound the bytes, not to be fair to the last
    /// emoji.
    /// </para>
    /// </remarks>
    public const int MaxDetailsLength = 1000;

    /// <summary>Longest note a reviewer may leave when acting on a flag.</summary>
    public const int MaxReviewerNoteLength = 1000;

    private static readonly FlagReason[] ImageReasons =
    [
        FlagReason.ImageArtistKnown,
        FlagReason.ImageAttributionMissing,
        FlagReason.ImageReplacementWanted,
        FlagReason.ImageRightsComplaint,
        FlagReason.ImageWrongSubject,
        FlagReason.Other,
    ];

    private static readonly FlagReason[] DocumentReasons =
    [
        FlagReason.TextError,
        FlagReason.ContentIncorrect,
        FlagReason.ContentMissing,
        FlagReason.SourceAttribution,
        FlagReason.Other,
    ];

    /// <summary>The reasons that may be raised against a picture.</summary>
    public static IReadOnlyList<FlagReason> ReasonsForImages => ImageReasons;

    /// <summary>The reasons that may be raised against a content document.</summary>
    public static IReadOnlyList<FlagReason> ReasonsForDocuments => DocumentReasons;

    /// <summary>
    /// Which kind of thing a reason is about.
    /// </summary>
    /// <remarks>
    /// <see cref="FlagReason.Other"/> is the one value that belongs to both, so
    /// it takes the kind from the target instead of deciding it. Every other
    /// reason decides, which is what lets the client send a reason and a key and
    /// nothing else.
    /// </remarks>
    public static FlagTargetKind? KindOf(FlagReason reason) => reason switch
    {
        FlagReason.ImageArtistKnown => FlagTargetKind.Image,
        FlagReason.ImageAttributionMissing => FlagTargetKind.Image,
        FlagReason.ImageReplacementWanted => FlagTargetKind.Image,
        FlagReason.ImageRightsComplaint => FlagTargetKind.Image,
        FlagReason.ImageWrongSubject => FlagTargetKind.Image,
        FlagReason.TextError => FlagTargetKind.Document,
        FlagReason.ContentIncorrect => FlagTargetKind.Document,
        FlagReason.ContentMissing => FlagTargetKind.Document,
        FlagReason.SourceAttribution => FlagTargetKind.Document,
        _ => null,
    };

    /// <summary>Whether <paramref name="reason"/> may be raised against <paramref name="kind"/>.</summary>
    public static bool Permits(FlagReason reason, FlagTargetKind kind) =>
        KindOf(reason) is not { } required || required == kind;

    /// <summary>Free text is only compulsory when the reason says nothing.</summary>
    public static bool RequiresDetails(FlagReason reason) => reason == FlagReason.Other;

    /// <summary>Whether the report is still outstanding work.</summary>
    public static bool IsOutstanding(FlagStatus status) =>
        status is FlagStatus.Open or FlagStatus.Accepted;

    /// <summary>
    /// Whether a reviewer may move a flag from <paramref name="from"/> to
    /// <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rules, and everything else falls out of them. A flag cannot jump
    /// from <see cref="FlagStatus.Declined"/> straight to
    /// <see cref="FlagStatus.Resolved"/>, because the second claims work was
    /// done on something the first said needed none, and a queue that permits
    /// that has a status field nobody can trust. And every terminal state can
    /// be reopened, because reviewers are wrong sometimes and a queue with no
    /// way back is a queue people are afraid to triage quickly.
    /// </para>
    /// <para>
    /// Restating the current status is refused rather than treated as a
    /// success. It is almost always a double submit or two reviewers acting on
    /// the same row, and answering 200 to the second one tells them they did
    /// something they did not do.
    /// </para>
    /// </remarks>
    public static bool CanTransition(FlagStatus from, FlagStatus to) => (from, to) switch
    {
        (FlagStatus.Open, FlagStatus.Accepted) => true,
        (FlagStatus.Open, FlagStatus.Declined) => true,

        (FlagStatus.Accepted, FlagStatus.Resolved) => true,
        (FlagStatus.Accepted, FlagStatus.Declined) => true,
        (FlagStatus.Accepted, FlagStatus.Open) => true,

        (FlagStatus.Declined, FlagStatus.Open) => true,
        (FlagStatus.Resolved, FlagStatus.Open) => true,

        _ => false,
    };
}
