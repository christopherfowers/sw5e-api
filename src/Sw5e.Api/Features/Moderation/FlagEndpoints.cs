using Microsoft.AspNetCore.RateLimiting;
using Sw5e.Api.Security;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Moderation;

/// <summary>
/// The flagging API: raising a report, reading your own, and working the queue.
/// </summary>
/// <remarks>
/// <para>
/// Read this route table the way the account one is meant to be read. Every
/// route states who may call it and which budget it draws on, and none of it is
/// inherited except the cross-site defence, which is applied at the group so a
/// route added later cannot forget it.
/// </para>
/// <para>
/// <b>Nothing here is anonymous.</b> That is the current policy and not an
/// architectural limit, and the distinction is deliberate: the intention is to
/// open reporting to the wider community, and when that happens it should be a
/// change to one attribute on one route rather than a redesign. The pieces that
/// make it a one-line change are already in place — the per-caller limiter
/// keyed on address, the target-existence check, the bounded free text, the
/// duplicate index — because every one of them is what an anonymous endpoint
/// would need, and building them later would mean building them under pressure.
/// </para>
/// <para>
/// What would genuinely have to be decided at that point is the two things that
/// currently come free with a session: who a duplicate is measured against, and
/// what the reporter's name is on the queue. Both have answers; neither has one
/// yet, which is why the route says Community accounts and above rather than
/// everybody.
/// </para>
/// </remarks>
internal static class FlagEndpoints
{
    public static IEndpointRouteBuilder MapFlagEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/flags")
            .WithTags("Flags")
            .AddEndpointFilter<CrossSiteRequestFilter>();

        MapReporting(group);
        MapQueue(group);

        return routes;
    }

    private static void MapReporting(RouteGroupBuilder group)
    {
        // Any signed-in account, whatever its role. Reporting a problem is not
        // a privilege — the whole value of it is that the people who notice a
        // wrong picture are readers rather than contributors — so the only bar
        // is an account, which is what makes a duplicate measurable and a
        // quota enforceable.
        group.MapPost("", FlagHandlers.RaiseAsync)
             .WithName("raiseFlag")
             .WithSummary("Report a problem with a page or a picture.")
             .WithDescription(
                 "Files a report against one content document. A picture is reported through " +
                 "its attribution record — content type asset-credit, key {group}-{key} — which " +
                 "is both what identifies the image and what a reviewer edits to resolve it. " +
                 "The reason decides whether the report is about a picture or about writing, so " +
                 "a picture reason against a rules chapter is refused rather than filed. The " +
                 "target must exist: a report pointing at nothing could never be reviewed or " +
                 "closed. Free text is optional except for \"other\", capped, and stored exactly " +
                 "as sent.")
             .Produces<FlagResponse>(StatusCodes.Status201Created)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
             .RequireRateLimiting(FlagRateLimiting.SubmitPolicy);

        // Deliberately not gated on a role, and deliberately not the queue with
        // a filter on it. A reporter sees what they filed and what became of
        // it; that is what stops reporting feeling like writing into a void,
        // and it is the reason this is a separate route rather than a query
        // parameter on the queue — a parameter would put the two audiences one
        // typo apart.
        group.MapGet("/mine", FlagHandlers.ListMineAsync)
             .WithName("listOwnFlags")
             .WithSummary("The reports you have filed.")
             .WithDescription(
                 "Your own reports, newest first, with the state each has reached. Reviewers' " +
                 "notes are not included: they are written between the people working the queue.")
             .Produces<FlagListResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
             .RequireRateLimiting(FlagRateLimiting.ReadPolicy);
    }

    private static void MapQueue(RouteGroupBuilder group)
    {
        // Contributors and administrators, and — because the policy carries it
        // — only from a session established with a passkey or an authenticator
        // code. The queue holds the display names of everybody who has reported
        // anything and the text of what they wrote, some of which will be
        // rights complaints. That is not something a session which only proved
        // control of a mailbox should open.
        group.MapGet("", FlagHandlers.ListAsync)
             .WithName("listFlags")
             .WithSummary("The review queue.")
             .WithDescription(
                 "Reports awaiting review, filtered by status, reason, target kind or target. " +
                 "Defaults to the outstanding ones — open and accepted — because a queue that " +
                 "opens on every report ever filed is a queue whose first page is useless. Pass " +
                 "status=all for everything. Rights complaints sort ahead of everything else; " +
                 "the rest are newest first.")
             .Produces<FlagListResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(FlagRateLimiting.ReadPolicy);

        group.MapGet("/summary", FlagHandlers.SummariseAsync)
             .WithName("summariseFlags")
             .WithSummary("The shape of the queue.")
             .WithDescription(
                 "Counts by status and by reason, and the documents carrying the most " +
                 "outstanding reports. This is how the queue is entered: roughly a hundred and " +
                 "fifty of the site's pictures have no recorded artist, so the raw list will be " +
                 "long and repetitive, and one typo report in the middle of it would never be " +
                 "seen by anybody paging through in date order.")
             .Produces<FlagSummaryResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(FlagRateLimiting.ReadPolicy);

        group.MapPut("/{flagId:guid}/status", FlagHandlers.UpdateStatusAsync)
             .WithName("updateFlagStatus")
             .WithSummary("Move a report through the lifecycle.")
             .WithDescription(
                 "open to accepted or declined; accepted to resolved, declined, or back to " +
                 "open; and either finished state back to open. Declined straight to resolved " +
                 "is refused, because it would claim work was done on something a reviewer had " +
                 "just said needed none. Restating the current status is refused too: it is " +
                 "almost always two reviewers acting on the same row, and answering 200 would " +
                 "tell the second one they did something they did not do. Records who acted " +
                 "and when.")
             .Produces<FlagResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(FlagRateLimiting.ReadPolicy);
    }
}
