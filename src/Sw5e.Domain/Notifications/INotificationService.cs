namespace Sw5e.Domain.Notifications;

/// <summary>
/// Telling people what happened, and letting them read it back.
/// </summary>
/// <remarks>
/// Writing is deliberately not "send to these accounts". The caller says what
/// happened and who did it; who hears about it is decided here, from the rules
/// in <see cref="NotificationRecipients"/>. A caller that chose its own
/// audience would be an editorial decision made at a call site, and the third
/// one written would disagree with the first two.
/// </remarks>
public interface INotificationService
{
    /// <summary>
    /// Records that something happened, and tells whoever should hear about it.
    /// </summary>
    /// <remarks>
    /// Never throws for want of an audience. A publish on a deployment with one
    /// administrator, who is the person publishing, notifies nobody, and that is
    /// a correct outcome rather than a failure: the work is published either
    /// way, and a publish that failed because the bell had nobody to ring would
    /// be absurd.
    /// </remarks>
    Task WriteAsync(
        NotificationEvent happening,
        CancellationToken cancellationToken = default);

    /// <summary>How many this account has not read.</summary>
    /// <remarks>
    /// Its own method, and its own endpoint, because the header asks for it far
    /// more often than anything asks for the list and it should answer from an
    /// index without reading rows.
    /// </remarks>
    Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>One page of this account's notifications, newest first.</summary>
    Task<IReadOnlyList<Notification>> ListAsync(
        Guid userId,
        bool unreadOnly,
        int limit,
        long? before,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks some of this account's notifications read.
    /// </summary>
    /// <returns>How many actually changed, which is not how many were asked for.</returns>
    /// <remarks>
    /// Scoped to <paramref name="userId"/> inside the implementation rather than
    /// trusted from the caller. Marking by id is one missing predicate away from
    /// letting somebody mark another account's notifications read, and the
    /// person it happened to would never know.
    /// </remarks>
    Task<int> MarkReadAsync(
        Guid userId,
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>Marks everything this account has not read as read.</summary>
    Task<int> MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Something worth telling somebody about.
/// </summary>
/// <param name="Kind">From the closed set in <see cref="NotificationKind"/>.</param>
/// <param name="ActorUserId">
/// Who did it. Excluded from the audience: a bell that lights up because you
/// pressed Publish teaches people to ignore it.
/// </param>
/// <param name="SubjectUserId">
/// The account the event is about, for the kinds that are about an account
/// rather than about a document.
/// </param>
/// <param name="SubjectType">Content type, when this is about content.</param>
/// <param name="SubjectKey">Document key, likewise.</param>
/// <param name="Detail">
/// What the line needs to read: display names, revision numbers. Never a
/// document body, and never somebody's prose about another person.
/// </param>
public sealed record NotificationEvent(
    string Kind,
    Guid? ActorUserId,
    Guid? SubjectUserId,
    string? SubjectType,
    string? SubjectKey,
    IReadOnlyDictionary<string, string> Detail);

/// <summary>Which audience each kind goes to.</summary>
/// <remarks>
/// A compiled table, in the same spirit as the content type registry, rather
/// than rows somebody edits. Who learns what is an editorial decision and
/// belongs in review, not in the hands of whoever holds the database.
/// </remarks>
public static class NotificationRecipients
{
    private static readonly Dictionary<string, NotificationAudience> Table = new(StringComparer.Ordinal)
    {
        [NotificationKind.ContentPublished] = NotificationAudience.Administrators,
        [NotificationKind.ContentReverted] = NotificationAudience.Administrators,
        [NotificationKind.FlagRaised] = NotificationAudience.Administrators,
        [NotificationKind.FlagResolved] = NotificationAudience.Administrators,
        [NotificationKind.RoleGranted] = NotificationAudience.SubjectAccount,
        [NotificationKind.AccountSuspended] = NotificationAudience.SubjectAccount,
    };

    /// <summary>
    /// Who hears about this kind, or null when nothing does.
    /// </summary>
    /// <remarks>
    /// Null for an unrecognised kind rather than a default audience. Defaulting
    /// to the administrators would mean a typo in a kind quietly mails
    /// everybody who can publish; defaulting to nobody would hide a real event.
    /// Neither is a guess worth making, so the caller is told it does not know.
    /// </remarks>
    public static NotificationAudience? For(string kind) =>
        Table.TryGetValue(kind, out var audience) ? audience : null;
}

/// <summary>
/// The accounts an audience resolves to.
/// </summary>
/// <remarks>
/// Declared here and implemented in the composition layer, which is the only
/// place that knows both what a notification is and how an account comes to
/// hold a role. The notification service knows a publish is for the
/// administrators; it has no business knowing what makes somebody one, and a
/// copy of that rule in a second project is one that goes stale the first time
/// the ladder changes.
/// </remarks>
public interface INotificationAudienceReader
{
    Task<IReadOnlyCollection<Guid>> AdministratorIdsAsync(
        CancellationToken cancellationToken = default);
}
