using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sw5e.Domain.Content;
using Sw5e.Domain.Moderation;
using Sw5e.Identity;
using Sw5e.Infrastructure.Persistence.Moderation;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// The authoring handlers: draft, publish, history, revert.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these takes <c>IContentAuthoringStore?</c> rather than
/// <c>IContentAuthoringStore</c>. The store is registered only alongside the
/// database content store, so on a file-backed deployment the parameter arrives
/// null and the handler answers 503. Resolving it optionally rather than
/// mapping the routes conditionally keeps one route table for every
/// deployment — a client gets the same answer shape everywhere, and the reason
/// authoring is unavailable is stated instead of being indistinguishable from a
/// typo in the URL.
/// </para>
/// <para>
/// Authorization is not done here. It is on the routes, as policy names, where
/// it can be read alongside everything else about the endpoint — and the
/// policies themselves carry the second-factor requirement, so a handler cannot
/// forget it.
/// </para>
/// </remarks>
internal static class AuthoringHandlers
{
    public static Results<Ok<ContentSchemaResponse>, ProblemHttpResult> GetSchema(
        string type,
        [FromServices] IContentSchemaValidator? schemas)
    {
        // Resolved optionally like the store, and for the same reason: the
        // validator is registered alongside the database content store, so a
        // file-backed deployment answers the same 503 here as everywhere else
        // rather than a 500 about a missing service.
        if (schemas is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (!ContentTypeRegistry.TryResolve(type, out var definition))
        {
            return AuthoringProblems.UnknownType;
        }

        var version = schemas.CurrentVersion(definition);
        var document = schemas.Published(definition, version);

        if (document is null)
        {
            return AuthoringProblems.NoSchema;
        }

        // The canonical key, not the name the caller used. The registry accepts
        // the route segment too, and a client that asked with one spelling and
        // stored the answer under the other would decide the same type was two.
        return TypedResults.Ok(
            new ContentSchemaResponse(definition.Key, version, document.Value));
    }

    public static async Task<Results<Ok<DraftListResponse>, ProblemHttpResult>> ListDraftsAsync(
        [FromServices] IContentAuthoringStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        var drafts = await store.ListDraftsAsync(cancellationToken);

        return TypedResults.Ok(new DraftListResponse(
            [.. drafts.Select(draft => new DraftSummaryResponse(
                draft.ContentType,
                draft.ItemKey,
                draft.Name,
                draft.TargetExists,
                draft.BaseRevisionIsCurrent,
                draft.CreatedByUserId,
                draft.UpdatedByUserId,
                draft.ResolvesFlagId,
                draft.CreatedAt,
                draft.UpdatedAt))]));
    }

    public static async Task<Results<Ok<DraftResponse>, ProblemHttpResult>> GetDraftAsync(
        string type,
        string key,
        [FromServices] IContentAuthoringStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (!AuthoringRequestValidation.TryResolve(type, key, out var definition, out var problem))
        {
            return problem!;
        }

        var draft = await store.GetDraftAsync(definition!, key, cancellationToken);

        if (draft is null)
        {
            return AuthoringProblems.NotFound;
        }

        return TypedResults.Ok(new DraftResponse(
            draft.ContentType,
            draft.ItemKey,
            draft.Body,
            draft.CreatedByUserId,
            draft.UpdatedByUserId,
            draft.BaseRevisionId,
            draft.ResolvesFlagId,
            draft.CreatedAt,
            draft.UpdatedAt));
    }

    public static async Task<Results<NoContent, ProblemHttpResult>> SaveDraftAsync(
        string type,
        string key,
        SaveDraftRequest? request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        [FromServices] IContentAuthoringStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (request is null || request.Document.ValueKind == JsonValueKind.Undefined)
        {
            return AuthoringProblems.MissingBody;
        }

        if (await users.GetUserAsync(context.User) is not { } actor)
        {
            return AuthoringProblems.NotAuthenticated;
        }

        if (!AuthoringRequestValidation.TryResolve(type, key, out var definition, out var problem) ||
            !AuthoringRequestValidation.TryReadDocument(request.Document, out problem))
        {
            return problem!;
        }

        var result = await store.SaveDraftAsync(
            definition!,
            key,
            request.Document,
            actor.Id,
            request.ResolvesFlagId,
            cancellationToken);

        return result.Status == ContentAuthoringStatus.Succeeded
            ? TypedResults.NoContent()
            : AuthoringProblems.From(result);
    }

    public static async Task<Results<NoContent, ProblemHttpResult>> DiscardDraftAsync(
        string type,
        string key,
        [FromServices] IContentAuthoringStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (!AuthoringRequestValidation.TryResolve(type, key, out var definition, out var problem))
        {
            return problem!;
        }

        return await store.DiscardDraftAsync(definition!, key, cancellationToken)
            ? TypedResults.NoContent()
            : AuthoringProblems.NotFound;
    }

    public static async Task<Results<Ok<RevisionSummaryResponse>, ProblemHttpResult>> PublishAsync(
        string type,
        string key,
        AuthoringReasonRequest? request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        [FromServices] IContentAuthoringStore? store,
        Sw5eModerationDbContext moderation,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (await users.GetUserAsync(context.User) is not { } actor)
        {
            return AuthoringProblems.NotAuthenticated;
        }

        if (!AuthoringRequestValidation.TryResolve(type, key, out var definition, out var problem) ||
            !AuthoringRequestValidation.TryReadReason(request?.Reason, out var reason, out problem))
        {
            return problem!;
        }

        // Read before publishing: the draft carries the report it answers, and
        // publishing removes the draft.
        var draft = await store.GetDraftAsync(definition!, key, cancellationToken);

        var result = await store.PublishDraftAsync(
            definition!, key, actor.Id, reason, cancellationToken);

        if (result.Status != ContentAuthoringStatus.Succeeded)
        {
            return AuthoringProblems.From(result);
        }

        var revision = result.Revision!;

        loggerFactory.CreateLogger(LogCategories.Authoring).LogInformation(
            "Published {Type}/{Key} as revision {Revision} by account {UserId}.",
            revision.ContentType,
            revision.ItemKey,
            revision.Id,
            actor.Id);

        if (draft?.ResolvesFlagId is { } flagId)
        {
            await ResolveFlagAsync(moderation, flagId, revision.Id, actor.Id, clock, cancellationToken);
        }

        return TypedResults.Ok(ToSummaryResponse(revision));
    }

    public static async Task<Results<Ok<RevisionListResponse>, ProblemHttpResult>> ListRevisionsAsync(
        string type,
        string key,
        int? limit,
        [FromServices] IContentAuthoringStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (!AuthoringRequestValidation.TryResolve(type, key, out var definition, out var problem) ||
            !AuthoringRequestValidation.TryReadLimit(limit, out var take, out problem))
        {
            return problem!;
        }

        var revisions = await store.ListRevisionsAsync(definition!, key, take, cancellationToken);

        return TypedResults.Ok(new RevisionListResponse(
            [.. revisions.Select(ToSummaryResponse)]));
    }

    public static async Task<Results<Ok<RevisionResponse>, ProblemHttpResult>> GetRevisionAsync(
        string type,
        string key,
        long revisionId,
        [FromServices] IContentAuthoringStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (!AuthoringRequestValidation.TryResolve(type, key, out var definition, out var problem))
        {
            return problem!;
        }

        var revision = await store.GetRevisionAsync(definition!, key, revisionId, cancellationToken);

        if (revision is null)
        {
            return AuthoringProblems.NotFound;
        }

        return TypedResults.Ok(new RevisionResponse(
            revision.Id,
            revision.ContentType,
            revision.ItemKey,
            revision.Number,
            ContentAuthoringWire.From(revision.Action),
            revision.ActorUserId,
            revision.Reason,
            revision.SchemaVersion,
            revision.RevertedFromId,
            revision.CreatedAt,
            revision.Body));
    }

    public static async Task<Results<Ok<RevisionSummaryResponse>, ProblemHttpResult>> RevertAsync(
        string type,
        string key,
        RevertRequest? request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        [FromServices] IContentAuthoringStore? store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return AuthoringProblems.NotEnabled;
        }

        if (request is null)
        {
            return AuthoringProblems.MissingBody;
        }

        if (await users.GetUserAsync(context.User) is not { } actor)
        {
            return AuthoringProblems.NotAuthenticated;
        }

        if (!AuthoringRequestValidation.TryResolve(type, key, out var definition, out var problem) ||
            !AuthoringRequestValidation.TryReadReason(request.Reason, out var reason, out problem))
        {
            return problem!;
        }

        var result = await store.RevertAsync(
            definition!, key, request.RevisionId, actor.Id, reason, cancellationToken);

        if (result.Status != ContentAuthoringStatus.Succeeded)
        {
            return AuthoringProblems.From(result);
        }

        loggerFactory.CreateLogger(LogCategories.Authoring).LogInformation(
            "Reverted {Type}/{Key} to revision {Target} by account {UserId}.",
            type,
            key,
            request.RevisionId,
            actor.Id);

        return TypedResults.Ok(ToSummaryResponse(result.Revision!));
    }

    /// <summary>
    /// Marks the report this publication answered as resolved, pointing at the
    /// revision that did it.
    /// </summary>
    /// <remarks>
    /// Best effort, and deliberately so. The content change has already been
    /// committed in the content database; moderation is a different schema and
    /// may be a different database, so there is no transaction spanning the two
    /// and pretending otherwise would be worse than not trying. A report left
    /// open next to a published fix is a reviewer clicking one more button. A
    /// publication rolled back because the moderation database was briefly
    /// unreachable would be a lost edit.
    /// </remarks>
    private static async Task ResolveFlagAsync(
        Sw5eModerationDbContext moderation,
        Guid flagId,
        long revisionId,
        Guid reviewerId,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var flag = await moderation.ContentFlags
            .SingleOrDefaultAsync(candidate => candidate.Id == flagId, cancellationToken);

        if (flag is null)
        {
            return;
        }

        // Only a report someone had already accepted is closed automatically.
        // An open report has not been triaged, and a declined one was a
        // decision somebody took; neither should be overturned as a side effect
        // of publishing something that happens to name it.
        if (flag.Status != FlagStatus.Accepted)
        {
            return;
        }

        flag.Status = FlagStatus.Resolved;
        flag.ReviewedByUserId = reviewerId;
        flag.ReviewedAt = clock.GetUtcNow();
        flag.ResolvedByRevisionId = revisionId;

        await moderation.SaveChangesAsync(cancellationToken);
    }

    private static RevisionSummaryResponse ToSummaryResponse(ContentRevisionSummary revision) =>
        new(revision.Id,
            revision.ContentType,
            revision.ItemKey,
            revision.Number,
            ContentAuthoringWire.From(revision.Action),
            revision.ActorUserId,
            revision.Reason,
            revision.RevertedFromId,
            revision.CreatedAt);
}
