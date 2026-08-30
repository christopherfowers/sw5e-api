using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Sw5e.Domain.Content;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// The read-only content API: the type registry, a browsable list per type, a
/// single item, and search across everything.
/// </summary>
/// <remarks>
/// Every endpoint here is anonymous and side-effect free. There is no write
/// surface by design; authoring arrives separately and will not be bolted onto
/// these routes.
/// </remarks>
public static class ContentEndpoints
{
    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api").WithTags("Content");

        group.MapGet("/content-types", GetContentTypesAsync)
             .WithName("listContentTypes")
             .WithSummary("List the content types.")
             .WithDescription(
                 "Every content type the API serves, with its display names, the slug the site " +
                 "uses in its own URLs, and how many items it currently holds. The navigation " +
                 "is built from this, so a type with no items is still listed, with a count of zero.")
             .Produces<ContentTypesResponse>()
             .Produces(StatusCodes.Status304NotModified)
             .AllowAnonymous();

        group.MapGet("/content/{type}", ListContentAsync)
             .WithName("listContent")
             .WithSummary("List the items of one content type.")
             .WithDescription(
                 "A page of one content type, filtered and ordered as asked. The total is " +
                 "returned alongside the page so a pager can be rendered without a second call. " +
                 "A page past the end is an empty page, not an error.")
             .Produces<ContentListResponse>()
             .Produces(StatusCodes.Status304NotModified)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .AllowAnonymous();

        group.MapGet("/content/{type}/{key}", GetContentItemAsync)
             .WithName("getContentItem")
             .WithSummary("Get one content item in full.")
             .WithDescription(
                 "The whole item, exactly as it validates against the published JSON Schema " +
                 "for its type.")
             .Produces<ContentItemResponse>()
             .Produces(StatusCodes.Status304NotModified)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .AllowAnonymous();

        group.MapGet("/search", SearchAsync)
             .WithName("searchContent")
             .WithSummary("Search every content type.")
             .WithDescription(
                 "Free-text search across the whole catalogue, grouped by content type. Each " +
                 "result carries the row fields needed to render it plus where the match was " +
                 "found, so the UI can show why a result is in the list.")
             .Produces<SearchResponse>()
             .Produces(StatusCodes.Status304NotModified)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .AllowAnonymous();

        return routes;
    }

    private static async Task<Results<Ok<ContentTypesResponse>, StatusCodeHttpResult>>
        GetContentTypesAsync(
            HttpContext context,
            IContentRepository repository,
            CancellationToken cancellationToken)
    {
        var types = await repository.GetContentTypesAsync(cancellationToken);

        var response = new ContentTypesResponse(
            [.. types.Select(type => new ContentTypeResponse(
                type.Key,
                type.DisplayName,
                type.PluralName,
                type.RouteSegment,
                type.ItemCount))]);

        // The registry changes only when the catalogue does, so its validator
        // is just the shape of the counts.
        var version = string.Join(
            '.',
            types.Select(type => $"{type.Key}{type.ItemCount}"));

        return ContentCaching.ApplyAndCheckFreshness(context, Hash(version))
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<ContentListResponse>, ProblemHttpResult, StatusCodeHttpResult>>
        ListContentAsync(
            HttpContext context,
            IContentRepository repository,
            string type,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? name,
            [FromQuery] string? source,
            [FromQuery] string? contentSet,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            CancellationToken cancellationToken)
    {
        // The route value is resolved against the registry before anything else
        // happens with it, and what is carried forward is the registry instance
        // rather than the caller's string.
        if (!ContentRequestValidation.TryResolveType(type, out var definition, out var typeProblem))
        {
            return typeProblem!;
        }

        if (!ContentRequestValidation.TryBuildListQuery(
                definition, page, pageSize, name, source, contentSet, sort, direction,
                out var query, out var queryProblem))
        {
            return queryProblem!;
        }

        var result = await repository.ListAsync(query, cancellationToken);

        if (ContentCaching.ApplyAndCheckFreshness(context, result.Version))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        var totalPages = result.TotalCount == 0
            ? 0
            : (int)Math.Ceiling(result.TotalCount / (double)result.PageSize);

        return TypedResults.Ok(new ContentListResponse(
            definition.Key,
            [.. result.Items.Select(ContentItemSummaryResponse.From)],
            new PageInfo(result.Page, result.PageSize, result.TotalCount, totalPages)));
    }

    private static async Task<Results<Ok<ContentItemResponse>, ProblemHttpResult, StatusCodeHttpResult>>
        GetContentItemAsync(
            HttpContext context,
            IContentRepository repository,
            string type,
            string key,
            CancellationToken cancellationToken)
    {
        if (!ContentRequestValidation.TryResolveType(type, out var definition, out var typeProblem))
        {
            return typeProblem!;
        }

        if (!ContentRequestValidation.TryValidateKey(key, out var validKey, out var keyProblem))
        {
            return keyProblem!;
        }

        var document = await repository.GetAsync(definition, validKey, cancellationToken);

        if (document is null)
        {
            return TypedResults.Problem(
                title: "Content not found",
                detail: $"No {definition.DisplayName.ToLowerInvariant()} with that key exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return ContentCaching.ApplyAndCheckFreshness(context, document.Version)
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.Ok(new ContentItemResponse(
                document.Type,
                document.Key,
                document.Name,
                document.Body));
    }

    private static async Task<Results<Ok<SearchResponse>, ProblemHttpResult, StatusCodeHttpResult>>
        SearchAsync(
            HttpContext context,
            IContentRepository repository,
            [FromQuery] string? q,
            [FromQuery] string? types,
            [FromQuery] int? limit,
            CancellationToken cancellationToken)
    {
        if (!ContentRequestValidation.TryBuildSearchQuery(q, types, limit, out var query, out var problem))
        {
            return problem!;
        }

        var result = await repository.SearchAsync(query, cancellationToken);

        if (ContentCaching.ApplyAndCheckFreshness(context, result.Version))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Ok(new SearchResponse(
            result.Query,
            result.TotalMatches,
            [.. result.Groups.Select(group => new SearchGroupResponse(
                group.Type,
                group.DisplayName,
                group.PluralName,
                group.RouteSegment,
                group.TotalMatches,
                [.. group.Hits.Select(SearchResultResponse.From)]))]));
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)))[..16];
}
