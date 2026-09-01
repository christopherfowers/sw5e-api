using System.Text.Json;

namespace Sw5e.Domain.Content;

/// <summary>
/// Checks a proposed content document against the published JSON Schema for its
/// type.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the domain, and the authoring store depends on it directly,
/// because validation is a rule about what the corpus is allowed to contain
/// rather than a rule about what a request is allowed to say. Putting it at the
/// endpoint would leave it in exactly one code path: the next writer — a bulk
/// import, a migration backfill, a repair script — would reach the store
/// without passing through it, and the corpus would be degraded by the tool
/// written to improve it.
/// </para>
/// <para>
/// The one implementation delegates to the validator in the content repository
/// itself. That is deliberate and is the whole point of the seam: the same
/// evaluation that gates a pull request against the corpus gates a write here,
/// so a document cannot be accepted by one and rejected by the other.
/// </para>
/// </remarks>
public interface IContentSchemaValidator
{
    /// <summary>
    /// The schema version documents of <paramref name="type"/> are currently
    /// written against.
    /// </summary>
    /// <remarks>
    /// Returned rather than assumed so that a revision can record which schema
    /// it was judged against. A document that was valid under v1 and is invalid
    /// under v2 is a migration problem, and telling the two apart afterwards is
    /// only possible if the version is written down at the time.
    /// </remarks>
    int CurrentVersion(ContentTypeDefinition type);

    /// <summary>
    /// Validates <paramref name="body"/> against <paramref name="type"/>'s
    /// schema at <paramref name="version"/>.
    /// </summary>
    ContentValidation Validate(ContentTypeDefinition type, int version, JsonElement body);
}

/// <summary>The outcome of validating one document.</summary>
/// <param name="IsValid">Whether the document conforms.</param>
/// <param name="Errors">
/// One entry per failed assertion, each naming the location in the document and
/// the keyword that rejected it. Empty when <paramref name="IsValid"/> is true.
/// </param>
/// <remarks>
/// Errors are carried out to the caller rather than logged and swallowed. A
/// contributor who is told only that their document is wrong will guess, and
/// the guess costs a reviewer's time; a contributor told which field failed
/// which constraint fixes it themselves. These strings describe the document
/// the caller just sent, so they disclose nothing the caller did not supply.
/// </remarks>
public sealed record ContentValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    /// <summary>A document that conforms.</summary>
    public static ContentValidation Valid { get; } = new(true, []);

    /// <summary>A document that does not, with the reasons.</summary>
    public static ContentValidation Invalid(IReadOnlyList<string> errors) => new(false, errors);
}
