using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sw5e.Domain.Notifications;

namespace Sw5e.Infrastructure.Persistence.Notifications;

/// <summary>
/// Resolves who should hear about something, and writes them each a row.
/// </summary>
/// <remarks>
/// The audience comes from <see cref="NotificationRecipients"/> and the accounts
/// in it come from <see cref="INotificationAudienceReader"/>, which identity
/// implements. This project cannot reference identity directly, and should not:
/// a notification is written about events in three different schemas and
/// belongs to none of them.
/// </remarks>
public sealed class NotificationService(
    IDbContextFactory<Sw5eNotificationDbContext> contextFactory,
    INotificationAudienceReader audience,
    TimeProvider clock,
    ILogger<NotificationService> logger) : INotificationService
{
    /// <summary>
    /// How long a read notification is kept before the sweep removes it.
    /// </summary>
    /// <remarks>
    /// Unread ones are never swept. Somebody who has not signed in for a year
    /// should still find out that their role changed.
    /// </remarks>
    public static readonly TimeSpan KeepReadFor = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public async Task WriteAsync(
        NotificationEvent happening,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(happening);

        if (!NotificationKind.IsKnown(happening.Kind))
        {
            /*
              Logged and dropped rather than thrown.

              This is called from the end of a publish, after the revision is
              written and the work is safe. Throwing here would turn a
              successful publish into a failed request over a notification the
              author never asked for, and they would publish again, which is
              the behaviour this whole feature exists to stop.
            */
            logger.LogWarning(
                "Notification kind {Kind} is not one this platform writes; nothing sent.",
                happening.Kind);
            return;
        }

        var recipients = await ResolveAsync(happening, cancellationToken);

        if (recipients.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var detail = JsonSerializer.Serialize(happening.Detail);

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        foreach (var recipient in recipients)
        {
            database.Notifications.Add(new NotificationRow
            {
                UserId = recipient,
                Kind = happening.Kind,
                ActorUserId = happening.ActorUserId,
                SubjectType = happening.SubjectType,
                SubjectKey = happening.SubjectKey,
                Detail = detail,
                CreatedAt = now,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<Guid>> ResolveAsync(
        NotificationEvent happening,
        CancellationToken cancellationToken)
    {
        switch (NotificationRecipients.For(happening.Kind))
        {
            case NotificationAudience.Administrators:
                var administrators = await audience.AdministratorIdsAsync(cancellationToken);

                // The actor is never told what they just did.
                return [.. administrators.Where(id => id != happening.ActorUserId)];

            case NotificationAudience.SubjectAccount:
                return happening.SubjectUserId is { } subject && subject != happening.ActorUserId
                    ? [subject]
                    : [];

            default:
                return [];
        }
    }

    /// <inheritdoc />
    public async Task<int> CountUnreadAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await database.Notifications
            .AsNoTracking()
            .CountAsync(row => row.UserId == userId && row.ReadAt == null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Domain.Notifications.Notification>> ListAsync(
        Guid userId,
        bool unreadOnly,
        int limit,
        long? before,
        CancellationToken cancellationToken = default)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = database.Notifications
            .AsNoTracking()
            .Where(row => row.UserId == userId);

        if (unreadOnly) query = query.Where(row => row.ReadAt == null);

        // Paged on the key rather than on an offset. An offset shifts under a
        // reader whose notifications keep arriving while they page, which on a
        // newest-first list means seeing one twice and missing another.
        if (before is { } cursor) query = query.Where(row => row.Id < cursor);

        var rows = await query
            .OrderByDescending(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(Describe)];
    }

    /// <inheritdoc />
    public async Task<int> MarkReadAsync(
        Guid userId,
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return 0;

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        /*
          Scoped to the account here rather than trusting the caller.

          Marking by id with no owner predicate lets anybody mark anybody's
          notifications read, and the person it happened to would never find
          out: the row is gone from their unread count and looks like something
          they dismissed themselves.
        */
        return await database.Notifications
            .Where(row => row.UserId == userId && row.ReadAt == null && ids.Contains(row.Id))
            .ExecuteUpdateAsync(
                set => set.SetProperty(row => row.ReadAt, clock.GetUtcNow()),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> MarkAllReadAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await database.Notifications
            .Where(row => row.UserId == userId && row.ReadAt == null)
            .ExecuteUpdateAsync(
                set => set.SetProperty(row => row.ReadAt, clock.GetUtcNow()),
                cancellationToken);
    }

    private static Domain.Notifications.Notification Describe(NotificationRow row) =>
        new(
            row.Id,
            row.UserId,
            row.Kind,
            row.ActorUserId,
            row.SubjectType,
            row.SubjectKey,
            Detail(row.Detail),
            row.CreatedAt,
            row.ReadAt);

    /// <summary>
    /// The stored detail, or nothing when it cannot be read.
    /// </summary>
    /// <remarks>
    /// A row whose detail will not parse still renders: the kind, the subject
    /// and the time are columns and are enough for a line somebody can act on.
    /// Throwing would take out the whole page of notifications over one bad
    /// row, which is a poor trade for a field that is only ever the wording.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> Detail(string stored)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
