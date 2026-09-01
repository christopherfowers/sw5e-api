using Microsoft.AspNetCore.RateLimiting;
using Sw5e.Api.Security;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// The authoring API: drafting a change, publishing it, reading a document's
/// history, and putting an earlier version back.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the first endpoints on the platform that change published
/// content.</b> Everything about the route table is shaped by that.
/// </para>
/// <para>
/// <b>Two tiers, not one.</b> Drafting is <c>sw5e:contribute</c>; publishing
/// and reverting are <c>sw5e:administer</c>. That split is the whole point of
/// having a draft state: a contributor is someone trusted to write canonical
/// rules, not someone trusted to put them live unreviewed, and collapsing the
/// two would mean the review step existed only by convention. It matches the
/// role table in the platform design, which says a contributor may "author and
/// submit" but "cannot self-publish canonical".
/// </para>
/// <para>
/// <b>Both policies already require a second factor used in this session.</b>
/// <c>sw5e:contribute</c> and <c>sw5e:administer</c> each carry
/// <c>StrongAuthenticationRequirement</c>, so a contributor who signed in with
/// an emailed code holds the role and is still refused — with
/// <c>strong-authentication-required</c>, so the client can say what to do
/// about it rather than showing a bare 403. Nothing in this file re-states that
/// rule; it comes with the policy, which is what stops a route added later from
/// quietly opting out of it.
/// </para>
/// <para>
/// <b>The cross-site filter is applied at the group</b>, exactly as on the
/// account and flag groups, so every unsafe method here is refused unless it
/// carries <c>Sec-Fetch-Site: same-origin</c> or an allowed <c>Origin</c>. For a
/// cookie-authenticated API that is the CSRF defence, and a write endpoint that
/// forgot it would let any page on the internet publish rules through a signed-in
/// administrator's browser.
/// </para>
/// <para>
/// <b>Rate limited despite the small, vetted population.</b> The accounts that
/// can reach these routes are few and strongly authenticated, so the limiter is
/// not really about abuse from strangers — it is about what a single stolen
/// session can do before anyone notices, and about a client bug that retries a
/// publish in a loop. The standard authenticated budget is the right size for
/// work a person does by hand.
/// </para>
/// <para>
/// <b>Reading history needs a role too.</b> A revision body is content that was
/// never published, or content that was withdrawn, and the actor identifiers
/// alongside it say who edited what. None of that is public in the way the
/// catalogue is.
/// </para>
/// </remarks>
internal static class AuthoringEndpoints
{
    public static IEndpointRouteBuilder MapAuthoringEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/authoring")
            .WithTags("Authoring")
            // Applied at the group so a route added later cannot forget it.
            .AddEndpointFilter<CrossSiteRequestFilter>();

        MapDrafts(group);
        MapHistory(group);

        return routes;
    }

    private static void MapDrafts(RouteGroupBuilder group)
    {
        group.MapGet("/drafts", AuthoringHandlers.ListDraftsAsync)
             .WithName("listContentDrafts")
             .WithSummary("The authoring worklist.")
             .WithDescription(
                 "Every outstanding draft, most recently touched first. Each entry says whether " +
                 "it would create a document or replace one, and whether the version it was " +
                 "started from is still current — a draft whose base is stale will be refused " +
                 "at publication rather than overwriting somebody else's work.")
             .Produces<DraftListResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapGet("/drafts/{type}/{key}", AuthoringHandlers.GetDraftAsync)
             .WithName("getContentDraft")
             .WithSummary("One draft, in full.")
             .WithDescription("The proposed document as it currently stands, with its provenance.")
             .Produces<DraftResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        // PUT rather than POST: a draft is one document at one address, and
        // saving it twice must leave one draft rather than two. The address is
        // the identity, so the operation is idempotent by construction.
        group.MapPut("/drafts/{type}/{key}", AuthoringHandlers.SaveDraftAsync)
             .WithName("saveContentDraft")
             .WithSummary("Create or replace a draft.")
             .WithDescription(
                 "Stores a proposed document without publishing it. The document is validated " +
                 "against the published JSON Schema for its content type on the way in, and a " +
                 "document that fails is refused with the failing locations — nothing is " +
                 "stored. The key in the URL and the document's own \"key\" property must " +
                 "agree. Optionally names the moderation report this work answers, which is " +
                 "what lets publishing it close that report.")
             .Produces(StatusCodes.Status204NoContent)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapDelete("/drafts/{type}/{key}", AuthoringHandlers.DiscardDraftAsync)
             .WithName("discardContentDraft")
             .WithSummary("Throw a draft away.")
             .WithDescription(
                 "Removes unpublished work. Published content and its history are untouched: " +
                 "there is no path from here to changing what readers see.")
             .Produces(StatusCodes.Status204NoContent)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        // Administrator only. This is the step that changes what the whole
        // community reads.
        group.MapPost("/drafts/{type}/{key}/publish", AuthoringHandlers.PublishAsync)
             .WithName("publishContentDraft")
             .WithSummary("Make a draft live.")
             .WithDescription(
                 "Validates the draft again, writes it to the catalogue, records a revision " +
                 "naming the account that published it, and clears the draft — in one " +
                 "transaction, so a refusal leaves nothing behind. Refused with 409 if the " +
                 "document has been published by somebody else since the draft was started. " +
                 "If the draft names a moderation report that a reviewer had already accepted, " +
                 "that report is resolved and pointed at the revision.")
             .Produces<RevisionSummaryResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);
    }

    private static void MapHistory(RouteGroupBuilder group)
    {
        group.MapGet("/content/{type}/{key}/revisions", AuthoringHandlers.ListRevisionsAsync)
             .WithName("listContentRevisions")
             .WithSummary("A document's history, newest first.")
             .WithDescription(
                 "Who changed this document, when, why, and what kind of change it was. " +
                 "Bodies are not included — fetch two revisions to build a diff. A document " +
                 "that has only ever been imported has no history until somebody edits it, at " +
                 "which point the state it was imported in is recorded as revision 1, " +
                 "attributed to nobody.")
             .Produces<RevisionListResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapGet(
                 "/content/{type}/{key}/revisions/{revisionId:long}",
                 AuthoringHandlers.GetRevisionAsync)
             .WithName("getContentRevision")
             .WithSummary("One revision, including the document as it then stood.")
             .WithDescription(
                 "The whole document at that point in its history. Scoped to the type and key " +
                 "in the URL, so one document's history cannot be read through another's " +
                 "address.")
             .Produces<RevisionResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Contribute)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        // Administrator only, for the same reason publishing is: it changes
        // what readers see.
        group.MapPost("/content/{type}/{key}/revert", AuthoringHandlers.RevertAsync)
             .WithName("revertContent")
             .WithSummary("Put an earlier version back.")
             .WithDescription(
                 "Restores the body of an earlier revision as a NEW revision, so the history " +
                 "records both the change and its undoing. The restored body is validated like " +
                 "any other write: a revision written under an older schema that no longer " +
                 "conforms is refused rather than quietly readmitted to the corpus.")
             .Produces<RevisionSummaryResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);
    }
}
