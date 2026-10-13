namespace Sw5e.Infrastructure.Persistence.Notifications;

/// <summary>
/// One thing somebody is being told, as a row.
/// </summary>
/// <remarks>
/// <para>
/// One row per recipient rather than one per event. The unread count is the
/// query the bell runs on every page view, and fanning out on write makes it a
/// single indexed count rather than a join against a table of read markers.
/// Each person also marks their own read without touching anybody else's.
/// </para>
/// <para>
/// The duplication that buys is small: the widest audience the platform has is
/// its administrators. If a kind is ever added whose audience is every account,
/// this write stops being trivial and wants batching, which is a reason to look
/// twice at any new kind whose recipients are not a role.
/// </para>
/// </remarks>
public sealed class NotificationRow
{
    public long Id { get; set; }

    /// <summary>
    /// Who is being told.
    /// </summary>
    /// <remarks>
    /// A user id with no foreign key, for the same reason the moderation tables
    /// carry one: identity is a different schema, written by a different
    /// pipeline and restored on its own schedule, and a constraint across that
    /// boundary makes one of them unrestorable without the other.
    /// </remarks>
    public Guid UserId { get; set; }

    /// <summary>What happened, from the closed set the domain declares.</summary>
    public required string Kind { get; set; }

    /// <summary>
    /// Who did it, or null when nothing did.
    /// </summary>
    /// <remarks>
    /// A retention sweep or a scheduled job has no actor, and naming one would
    /// be inventing a person. Null is read as "the system", which is honest.
    /// </remarks>
    public Guid? ActorUserId { get; set; }

    /// <summary>The content type this is about, when it is about content.</summary>
    public string? SubjectType { get; set; }

    /// <summary>The document key, likewise.</summary>
    public string? SubjectKey { get; set; }

    /// <summary>
    /// What the line needs in order to read, as a jsonb object of strings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried rather than joined. A notification is a statement about a
    /// moment: if a document is renamed afterwards, the old line should still
    /// read the way it read when it was written, and a join would quietly
    /// rewrite history every time somebody corrected a title.
    /// </para>
    /// <para>
    /// Display names and numbers only. Never a document body, and never the
    /// reason text from a moderation flag, which is somebody's account of
    /// another person's conduct and belongs on the flag where the people who
    /// may read it already are.
    /// </para>
    /// </remarks>
    public required string Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this was read, or null while it is still unread.
    /// </summary>
    /// <remarks>
    /// A timestamp rather than a flag, because the retention sweep deletes read
    /// notifications after thirty days and needs to know when. Unread ones are
    /// kept indefinitely: somebody who has not signed in for a year should
    /// still find out their role changed.
    /// </remarks>
    public DateTimeOffset? ReadAt { get; set; }
}
