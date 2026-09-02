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
    /// <remarks>
    /// <para>
    /// Two shapes of the same information, and both are sent. <c>schemaErrors</c>
    /// is one line per failure and is what every existing client reads;
    /// <c>schemaViolations</c> is the same failures with the location, the
    /// keyword and the message kept apart.
    /// </para>
    /// <para>
    /// The structured one exists because the editor was reconstructing it. The
    /// pointer is what lets an error be shown beside the control that caused
    /// it, and it was being recovered from the line with a regular expression —
    /// a guess at a format produced in a different repository, promised by
    /// nothing here. A reworded validator message would have quietly stopped
    /// errors landing on fields.
    /// </para>
    /// <para>
    /// Both are published rather than one replacing the other, because the
    /// browser application and this service are deployed as separate images
    /// and either can be ahead of the other. A client that only knows the lines
    /// keeps working; one that knows both prefers the structured field. It
    /// costs a few hundred bytes on a response nobody wanted.
    /// </para>
    /// </remarks>
    public static ProblemHttpResult SchemaViolation(
        IReadOnlyList<string> errors,
        IReadOnlyList<ContentViolation> violations) =>
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
                ["schemaViolations"] = violations
                    .Select(violation => new SchemaViolationDetail(
                        violation.InstanceLocation,
                        violation.Keyword,
                        violation.Message))
                    .ToArray(),
            });

    /// <summary>No content type by that name.</summary>
    public static ProblemHttpResult UnknownType =>
        TypedResults.Problem(
            title: "No such content type",
            detail: "The content type in the URL is not one this site publishes.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>The content type is real, and has no schema published.</summary>
    /// <remarks>
    /// Separate from <see cref="UnknownType"/> because they are different facts
    /// and a client acts differently on each. An unknown type is an address
    /// nobody should have built; a type with no schema is this deployment being
    /// packaged without one, and a client that generates an editor from schemas
    /// should fall back to editing the document directly rather than refusing
    /// to open.
    /// </remarks>
    public static ProblemHttpResult NoSchema =>
        TypedResults.Problem(
            title: "No schema is published for that content type",
            detail:
                "This deployment has no schema file for that type, so its shape cannot be " +
                "described. Documents of this type are still validated on the way in.",
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
        ContentAuthoringStatus.Invalid => SchemaViolation(result.Errors, result.Violations),
        ContentAuthoringStatus.NotFound => NotFound,
        ContentAuthoringStatus.Stale => Stale,
        _ => throw new ArgumentOutOfRangeException(
            nameof(result), result.Status, "That outcome is not a refusal."),
    };
}
