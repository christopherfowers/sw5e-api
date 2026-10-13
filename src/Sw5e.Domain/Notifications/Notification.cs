namespace Sw5e.Domain.Notifications;

/// <summary>
/// Something that happened, addressed to one person.
/// </summary>
/// <remarks>
/// <para>
/// A correction to a class was published and nobody was told. It was published
/// twice more over the following fortnight, by somebody reasonably concluding
/// the tool was broken. Every one of those publishes was recorded correctly;
/// there was simply nowhere that said so, and reading the record meant already
/// knowing which document to open.
/// </para>
/// <para>
/// One row per recipient, rather than one row per event with read markers
/// beside it. Fanning out on write makes the unread count a single indexed
/// count, which is the query the bell runs on every page, and lets each person
/// mark their own without a second table. The cost is duplication, and the
/// largest audience here is the administrators, which is a handful.
/// </para>
/// </remarks>
/// <param name="Id">Surrogate key.</param>
/// <param name="UserId">Who is being told.</param>
/// <param name="Kind">What happened, from the closed set in <see cref="NotificationKind"/>.</param>
/// <param name="ActorUserId">
/// Who did it, or null when nothing did: a retention sweep or a scheduled job
/// has no actor, and claiming one would be inventing a person.
/// </param>
/// <param name="SubjectType">The content type this is about, when it is about content.</param>
/// <param name="SubjectKey">The document key, likewise.</param>
/// <param name="Detail">
/// Everything the line needs in order to read, carried rather than joined.
///
/// A notification is a statement about a moment. If a document is renamed
/// afterwards the old line should still read the way it read when it was
/// written, and a join would quietly rewrite history. It holds display names
/// and numbers, never document bodies and never the reason text from a
/// moderation flag, which is somebody's account of another person's conduct
/// and belongs on the flag.
/// </param>
public sealed record Notification(
    long Id,
    Guid UserId,
    string Kind,
    Guid? ActorUserId,
    string? SubjectType,
    string? SubjectKey,
    IReadOnlyDictionary<string, string> Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>
/// The things somebody can be told about.
/// </summary>
/// <remarks>
/// A closed set, written out here. Adding one is a code change on purpose: who
/// learns what is an editorial decision and should go through review rather
/// than being editable by whoever holds the database.
/// </remarks>
public static class NotificationKind
{
    /// <summary>A document was published as a new revision.</summary>
    public const string ContentPublished = "content.published";

    /// <summary>A document was put back to an earlier revision.</summary>
    public const string ContentReverted = "content.reverted";

    /// <summary>Somebody reported a problem with a document.</summary>
    public const string FlagRaised = "flag.raised";

    /// <summary>A report was closed.</summary>
    public const string FlagResolved = "flag.resolved";

    /// <summary>This account's role changed.</summary>
    public const string RoleGranted = "account.role_granted";

    /// <summary>This account was suspended.</summary>
    public const string AccountSuspended = "account.suspended";

    private static readonly HashSet<string> Known =
    [
        ContentPublished,
        ContentReverted,
        FlagRaised,
        FlagResolved,
        RoleGranted,
        AccountSuspended,
    ];

    /// <summary>
    /// Whether this is a kind the platform writes.
    /// </summary>
    /// <remarks>
    /// Checked on the way in rather than trusted. A row with an unrecognised
    /// kind renders as nothing a reader can act on, and would sit in somebody's
    /// unread count forever.
    /// </remarks>
    public static bool IsKnown(string? kind) => kind is not null && Known.Contains(kind);
}

/// <summary>Who hears about what.</summary>
/// <remarks>
/// <para>
/// Derived from what somebody can already do, rather than from a list of
/// watchers kept by hand. A hand-kept list goes stale the first time an
/// account is promoted, and then the person who most needs telling is the one
/// nobody added.
/// </para>
/// <para>
/// Two rules hold across every kind. The actor is never told what they just
/// did, because a bell that lights up when you press Publish teaches people to
/// ignore it. And a notification is never sent to somebody who could not
/// already see the thing it is about.
/// </para>
/// </remarks>
public enum NotificationAudience
{
    /// <summary>Everyone who can publish, which is the administrators.</summary>
    Administrators,

    /// <summary>The one account the event is about.</summary>
    SubjectAccount,
}
