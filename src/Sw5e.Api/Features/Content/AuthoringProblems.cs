using Microsoft.AspNetCore.Http.HttpResults;
using Sw5e.Domain.Content;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// The refusals the authoring endpoints produce.
/// </summary>
/// <remarks>
/// <para>
/// Written out in one place so the whole set can be read at once, in the same
/// spirit as the flag endpoints' refusals and for the same reasons: a caller
/// here already holds a session with an elevated role, the content registry is
/// published in full, and nothing these messages could disclose is a
/// credential. Being specific costs nothing and saves a contributor from
/// guessing why a document was refused.
/// </para>
/// <para>
/// Schema errors are the exception worth naming. They are returned verbatim
/// from the validator, and they describe the document the caller has just sent
/// — a location and a keyword, not stored content — so they disclose nothing
/// the caller did not supply. They are carried in a dedicated
/// <c>schemaErrors</c> extension rather than concatenated into
/// <c>detail</c>, because an editor wants to put each one beside the field it
/// came from.
/// </para>
/// </remarks>
internal static class AuthoringProblems
{
    private const string Title = "That change could not be saved";

    /// <summary>The request body was absent or unreadable.</summary>
    public static ProblemHttpResult MissingBody =>
        TypedResults.Problem(
            title: Title,
            detail: "The request carried no document.",
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>A field of the request was wrong.</summary>
    public static ProblemHttpResult Invalid(string field, string detail) =>
        TypedResults.Problem(
            title: Title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["fieldErrors"] = new Dictionary<string, string> { [field] = detail },
            });

    /// <summary>The document did not conform to its type's schema.</summary>
    public static ProblemHttpResult SchemaViolation(IReadOnlyList<string> errors) =>
        TypedResults.Problem(
            title: Title,
            detail:
                "The document does not match the published schema for its content type, " +
                "so it was refused and nothing was stored.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "schema-violation",
                ["schemaErrors"] = errors,
            });

    /// <summary>No content type by that name.</summary>
    public static ProblemHttpResult UnknownType =>
        TypedResults.Problem(
            title: "No such content type",
            detail: "The content type in the URL is not one this site publishes.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>No draft, document or revision by that name.</summary>
    public static ProblemHttpResult NotFound =>
        TypedResults.Problem(
            title: "Nothing to work on",
            detail: "There is no draft, document or revision matching that address.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The document was published by somebody else while this draft was open.
    /// </summary>
    public static ProblemHttpResult Stale =>
        TypedResults.Problem(
            title: "That document has moved on",
            detail:
                "Somebody published a change to this document after this draft was started. " +
                "Nothing was overwritten. Re-open the draft against the current version and " +
                "reapply the edit.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = "draft-stale" });

    /// <summary>
    /// This deployment serves content from files, so there is nothing to write
    /// to.
    /// </summary>
    /// <remarks>
    /// 503 rather than 404, deliberately. A 404 would say authoring does not
    /// exist on this platform; the truth is that it exists and this deployment
    /// has not turned it on, which is a configuration fact an operator can act
    /// on and a client can distinguish from a wrong URL.
    /// </remarks>
    public static ProblemHttpResult NotEnabled =>
        TypedResults.Problem(
            title: "Content authoring is not enabled here",
            detail:
                "This deployment serves content from files, which are read-only at runtime. " +
                "Authoring requires the database content store.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?> { ["code"] = "authoring-unavailable" });

    /// <summary>The session vanished between authorization and the handler.</summary>
    public static ProblemHttpResult NotAuthenticated =>
        TypedResults.Problem(
            title: "Not signed in",
            detail: "This action needs a signed-in account.",
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Maps a store outcome onto its refusal.</summary>
    public static ProblemHttpResult From(ContentAuthoringResult result) => result.Status switch
    {
        ContentAuthoringStatus.Invalid => SchemaViolation(result.Errors),
        ContentAuthoringStatus.NotFound => NotFound,
        ContentAuthoringStatus.Stale => Stale,
        _ => throw new ArgumentOutOfRangeException(
            nameof(result), result.Status, "That outcome is not a refusal."),
    };
}
