using Microsoft.AspNetCore.Http.HttpResults;
using Sw5e.Domain.Content;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// Turns caller-supplied route and query values into validated domain queries,
/// or into a Problem Details response.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here echoes a filesystem path, a stack trace or an internal
/// identifier. Where a message needs to say what was allowed, it lists the
/// allowed values — which are already public, because the registry endpoint
/// serves them — rather than repeating what the caller sent.
/// </para>
/// <para>
/// Every method fails closed: an unparsable, out-of-range or unrecognised value
/// is an error, never a silently substituted default. The one exception is an
/// absent value, which takes the documented default.
/// </para>
/// </remarks>
internal static class ContentRequestValidation
{
    /// <summary>
    /// Resolves the <c>{type}</c> route value against the registry.
    /// </summary>
    /// <remarks>
    /// This is the gate that makes the rest of the pipeline safe. The value
    /// arrives from the URL and would otherwise reach a path join in the
    /// filesystem store, so it is never passed onward: what comes back is a
    /// registry instance built from compile-time constants. Anything not in the
    /// registry — including <c>..</c>, an encoded separator, or a plausible but
    /// unknown type — resolves to nothing and produces a 404.
    /// </remarks>
    public static bool TryResolveType(
        string? type,
        out ContentTypeDefinition definition,
        out ProblemHttpResult? problem)
    {
        if (ContentTypeRegistry.TryResolve(type, out var resolved))
        {
            definition = resolved;
            problem = null;
            return true;
        }

        definition = null!;
        problem = TypedResults.Problem(
            title: "Unknown content type",
            detail: "No content type with that name exists. Call /api/content-types for the ones that do.",
            statusCode: StatusCodes.Status404NotFound);

        return false;
    }

    /// <summary>Validates the <c>{key}</c> route value against the slug format.</summary>
    /// <remarks>
    /// A malformed key is a 400 rather than a 404, because the request itself is
    /// wrong: no key of that shape can exist. Keeping the two apart also means
    /// the traversal attempt and the honest typo are distinguishable in the
    /// access log.
    /// </remarks>
    public static bool TryValidateKey(string? key, out string validated, out ProblemHttpResult? problem)
    {
        if (ContentSlug.IsValid(key))
        {
            validated = key!;
            problem = null;
            return true;
        }

        validated = string.Empty;
        problem = TypedResults.Problem(
            title: "Invalid content key",
            detail:
                "A content key is lowercase letters and digits in hyphen-separated groups, " +
                $"up to {ContentSlug.MaxLength} characters.",
            statusCode: StatusCodes.Status400BadRequest);

        return false;
    }

    /// <summary>Builds a validated list query from the query string.</summary>
    public static bool TryBuildListQuery(
        ContentTypeDefinition type,
        int? page,
        int? pageSize,
        string? name,
        string? source,
        string? contentSet,
        string? sort,
        string? direction,
        out ContentListQuery query,
        out ProblemHttpResult? problem)
    {
        query = null!;
        problem = null;

        var resolvedPage = page ?? ContentRequestLimits.DefaultPage;

        if (resolvedPage < 1)
        {
            problem = BadRequest(
                "Invalid page",
                "The 'page' parameter is 1-based, so the smallest valid value is 1.");
            return false;
        }

        var resolvedPageSize = pageSize ?? ContentRequestLimits.DefaultPageSize;

        if (resolvedPageSize < 1 || resolvedPageSize > ContentRequestLimits.MaxPageSize)
        {
            problem = BadRequest(
                "Invalid page size",
                $"The 'pageSize' parameter must be between 1 and {ContentRequestLimits.MaxPageSize}.");
            return false;
        }

        if (name is { Length: > ContentRequestLimits.MaxNameFilterLength })
        {
            problem = BadRequest(
                "Name filter too long",
                $"The 'name' filter may be at most {ContentRequestLimits.MaxNameFilterLength} characters.");
            return false;
        }

        if (!string.IsNullOrEmpty(source) && !ContentSlug.IsValid(source))
        {
            problem = BadRequest(
                "Invalid source filter",
                "The 'source' filter must be a source key: lowercase letters and digits in " +
                "hyphen-separated groups.");
            return false;
        }

        if (!TryParseContentSet(contentSet, out var resolvedContentSet, out problem) ||
            !TryParseSort(sort, out var sortField, out problem) ||
            !TryParseDirection(direction, out var sortDirection, out problem))
        {
            return false;
        }

        query = new ContentListQuery(
            type,
            Normalise(name),
            Normalise(source),
            resolvedContentSet,
            sortField,
            sortDirection,
            resolvedPage,
            resolvedPageSize);

        return true;
    }

    /// <summary>Builds a validated search query from the query string.</summary>
    public static bool TryBuildSearchQuery(
        string? q,
        string? types,
        int? limit,
        out ContentSearchQuery query,
        out ProblemHttpResult? problem)
    {
        query = null!;
        problem = null;

        var text = q?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length < ContentRequestLimits.MinSearchLength)
        {
            problem = BadRequest(
                "Invalid search query",
                $"The 'q' parameter is required and must be at least " +
                $"{ContentRequestLimits.MinSearchLength} characters.");
            return false;
        }

        if (text.Length > ContentRequestLimits.MaxSearchLength)
        {
            problem = BadRequest(
                "Search query too long",
                $"The 'q' parameter may be at most {ContentRequestLimits.MaxSearchLength} characters.");
            return false;
        }

        var resolvedLimit = limit ?? ContentRequestLimits.DefaultSearchLimit;

        if (resolvedLimit < 1 || resolvedLimit > ContentRequestLimits.MaxSearchLimit)
        {
            problem = BadRequest(
                "Invalid search limit",
                $"The 'limit' parameter must be between 1 and {ContentRequestLimits.MaxSearchLimit}.");
            return false;
        }

        if (!TryParseTypes(types, out var resolvedTypes, out problem))
        {
            return false;
        }

        query = new ContentSearchQuery(text, resolvedTypes, resolvedLimit);
        return true;
    }

    private static bool TryParseTypes(
        string? types,
        out IReadOnlyList<ContentTypeDefinition>? resolved,
        out ProblemHttpResult? problem)
    {
        resolved = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(types))
        {
            return true;
        }

        var names = types.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length > ContentRequestLimits.MaxSearchTypes)
        {
            problem = BadRequest(
                "Too many content types",
                $"The 'types' parameter may name at most {ContentRequestLimits.MaxSearchTypes} types.");
            return false;
        }

        var definitions = new List<ContentTypeDefinition>(names.Length);

        foreach (var name in names)
        {
            if (!ContentTypeRegistry.TryResolve(name, out var definition))
            {
                problem = BadRequest(
                    "Unknown content type",
                    "The 'types' parameter names a content type that does not exist. " +
                    "Call /api/content-types for the ones that do.");
                return false;
            }

            if (!definitions.Contains(definition))
            {
                definitions.Add(definition);
            }
        }

        resolved = definitions;
        return true;
    }

    private static bool TryParseContentSet(
        string? value,
        out string? resolved,
        out ProblemHttpResult? problem)
    {
        problem = null;
        resolved = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // A closed set from the schemas. Checked rather than passed through so
        // the filter can never become a channel for arbitrary text.
        if (value is "core" or "expanded-content")
        {
            resolved = value;
            return true;
        }

        problem = BadRequest(
            "Invalid content set",
            "The 'contentSet' filter must be 'core' or 'expanded-content'.");

        return false;
    }

    private static bool TryParseSort(
        string? value,
        out ContentSortField field,
        out ProblemHttpResult? problem)
    {
        problem = null;

        // Mapped through a closed switch, never parsed loosely and never
        // forwarded as a string: the sort field ends up in an ORDER BY once the
        // database store exists, and an unrecognised value that reached it
        // would be an injection point.
        switch (value)
        {
            case null or "":
            case "name":
                field = ContentSortField.Name;
                return true;

            case "key":
                field = ContentSortField.Key;
                return true;

            case "sourceKey":
                field = ContentSortField.SourceKey;
                return true;

            case "contentSet":
                field = ContentSortField.ContentSet;
                return true;

            default:
                field = ContentSortField.Name;
                problem = BadRequest(
                    "Unknown sort field",
                    "The 'sort' parameter must be one of: name, key, sourceKey, contentSet.");
                return false;
        }
    }

    private static bool TryParseDirection(
        string? value,
        out SortDirection direction,
        out ProblemHttpResult? problem)
    {
        problem = null;

        switch (value)
        {
            case null or "":
            case "asc":
                direction = SortDirection.Ascending;
                return true;

            case "desc":
                direction = SortDirection.Descending;
                return true;

            default:
                direction = SortDirection.Ascending;
                problem = BadRequest(
                    "Unknown sort direction",
                    "The 'direction' parameter must be 'asc' or 'desc'.");
                return false;
        }
    }

    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProblemHttpResult BadRequest(string title, string detail) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);
}
