using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sw5e.Domain.Notifications;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Notifications;

/// <summary>
/// Each account's own inbox.
/// </summary>
/// <remarks>
/// <para>
/// Every route here answers for the calling account and takes no account
/// parameter at all. There is deliberately no way to ask for somebody else's,
/// including for an administrator: an endpoint that accepted a user id would be
/// one missing check away from handing over another person's inbox, and a
/// feature nobody has asked for is not worth that.
/// </para>
/// <para>
/// The count has its own route because the header asks for it on every page
/// and the list is asked for only when somebody opens the panel. Serving a
/// number from an index is a different shape of request from serving twenty
/// rows, and collapsing them would make the common one pay for the rare one.
/// </para>
/// </remarks>
public static class NotificationEndpoints
{
    /// <summary>
    /// The most a single page may carry.
    /// </summary>
    /// <remarks>
    /// A cap rather than a suggestion. Without one, a caller asking for ten
    /// thousand gets a query that reads ten thousand rows, and the only person
    /// who benefits is whoever is probing for a way to make the database work
    /// hard on request.
    /// </remarks>
    public const int MaxPageSize = 50;

    public const int DefaultPageSize = 20;

    /// <summary>
    /// The most ids one mark-read call may name.
    /// </summary>
    /// <remarks>
    /// Matches the page cap, because the only legitimate caller is marking what
    /// it just displayed.
    /// </remarks>
    public const int MaxMarkBatch = MaxPageSize;

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/notifications")
                          .WithTags("Notifications")
                          .RequireAuthorization();

        group.MapGet("/count", CountAsync)
             .WithName("countNotifications")
             .WithSummary("How many the signed-in account has not read.")
             .WithDescription(
                 "Answered from an index without reading rows, because the site asks for this " +
                 "on every page and asks for the list itself only when somebody opens the panel.")
             .Produces<NotificationCountResponse>();

        group.MapGet("/", ListAsync)
             .WithName("listNotifications")
             .WithSummary("One page of the signed-in account's notifications, newest first.")
             .WithDescription(
                 "Paged on the id of the oldest row returned rather than on an offset. An " +
                 "offset shifts under a reader whose notifications keep arriving while they " +
                 "page, which on a newest-first list means seeing one twice and missing another.")
             .Produces<NotificationListResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/read", MarkReadAsync)
             .WithName("markNotificationsRead")
             .WithSummary("Marks some of the signed-in account's notifications read.")
             .WithDescription(
                 "Ids belonging to another account are ignored rather than refused: the count " +
                 "returned is how many actually changed, and a caller cannot use this to learn " +
                 "whether an id it does not own exists.")
             .Produces<NotificationMarkedResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/read-all", MarkAllReadAsync)
             .WithName("markAllNotificationsRead")
             .WithSummary("Marks everything the signed-in account has not read as read.")
             .Produces<NotificationMarkedResponse>();

        return routes;
    }

    private static async Task<Results<Ok<NotificationCountResponse>, UnauthorizedHttpResult>>
        CountAsync(
            HttpContext context,
            UserManager<Sw5eUser> users,
            INotificationService notifications,
            CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(context.User) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        /*
          Never cached.

          A count is per account and changes the moment anybody publishes. A
          shared cache would hand one administrator another's number, and even
          a private cache would leave the bell stale for as long as it lived,
          which is the one thing it must not be.
        */
        context.Response.Headers.CacheControl = "no-store";

        return TypedResults.Ok(new NotificationCountResponse(
            await notifications.CountUnreadAsync(account.Id, cancellationToken)));
    }

    private static async Task<Results<Ok<NotificationListResponse>, ProblemHttpResult, UnauthorizedHttpResult>>
        ListAsync(
            HttpContext context,
            UserManager<Sw5eUser> users,
            INotificationService notifications,
            [FromQuery] bool? unread,
            [FromQuery] int? limit,
            [FromQuery] long? before,
            CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(context.User) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        if (limit is { } asked && (asked < 1 || asked > MaxPageSize))
        {
            return TypedResults.Problem(
                title: "Invalid page size",
                detail: $"limit must be between 1 and {MaxPageSize}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        context.Response.Headers.CacheControl = "no-store";

        var rows = await notifications.ListAsync(
            account.Id,
            unread ?? false,
            limit ?? DefaultPageSize,
            before,
            cancellationToken);

        return TypedResults.Ok(new NotificationListResponse(
            [.. rows.Select(Describe)],
            // The cursor for the next page, or null at the end. Returned rather
            // than left for the caller to work out from the last id, so that
            // "there is no more" and "ask again from here" are different
            // answers instead of an inference.
            rows.Count == (limit ?? DefaultPageSize) ? rows[^1].Id : null));
    }

    private static async Task<Results<Ok<NotificationMarkedResponse>, ProblemHttpResult, UnauthorizedHttpResult>>
        MarkReadAsync(
            HttpContext context,
            UserManager<Sw5eUser> users,
            INotificationService notifications,
            MarkNotificationsReadRequest? request,
            CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(context.User) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        var ids = request?.Ids ?? [];

        if (ids.Count > MaxMarkBatch)
        {
            return TypedResults.Problem(
                title: "Too many notifications",
                detail: $"At most {MaxMarkBatch} may be marked read at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return TypedResults.Ok(new NotificationMarkedResponse(
            await notifications.MarkReadAsync(account.Id, ids, cancellationToken)));
    }

    private static async Task<Results<Ok<NotificationMarkedResponse>, UnauthorizedHttpResult>>
        MarkAllReadAsync(
            HttpContext context,
            UserManager<Sw5eUser> users,
            INotificationService notifications,
            CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(context.User) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(new NotificationMarkedResponse(
            await notifications.MarkAllReadAsync(account.Id, cancellationToken)));
    }

    private static NotificationResponse Describe(Notification row) =>
        new(
            row.Id,
            row.Kind,
            row.SubjectType,
            row.SubjectKey,
            row.Detail,
            row.CreatedAt,
            row.ReadAt is not null);
}

/// <summary>How many the signed-in account has not read.</summary>
public sealed record NotificationCountResponse(int Unread);

/// <summary>One page of notifications.</summary>
/// <param name="Items">The page, newest first.</param>
/// <param name="NextCursor">
/// What to pass as <c>before</c> for the following page, or null at the end.
/// </param>
public sealed record NotificationListResponse(
    IReadOnlyList<NotificationResponse> Items,
    long? NextCursor);

/// <summary>
/// One notification, as the site draws it.
/// </summary>
/// <remarks>
/// The actor's id is deliberately absent. The line needs the actor's display
/// name, which <paramref name="Detail"/> already carries, and handing out
/// account ids to every administrator who opens a bell is a wider disclosure
/// than the feature needs.
/// </remarks>
public sealed record NotificationResponse(
    long Id,
    string Kind,
    string? SubjectType,
    string? SubjectKey,
    IReadOnlyDictionary<string, string> Detail,
    DateTimeOffset CreatedAt,
    bool Read);

/// <summary>Which to mark read.</summary>
public sealed record MarkNotificationsReadRequest(IReadOnlyList<long> Ids);

/// <summary>How many actually changed.</summary>
public sealed record NotificationMarkedResponse(int Marked);
