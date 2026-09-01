using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Sw5e.Domain.Content;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// Turns the untrusted parts of an authoring request into resolved domain
/// values, or into the refusal.
/// </summary>
/// <remarks>
/// Separated from the handlers for the same reason the read path separates its
/// own: these are the checks that decide what a store is ever asked, and they
/// are worth reading as a set rather than scattered through six handlers. The
/// ordering inside each handler is cheapest-first, so nothing below costs a
/// database round trip until the request has been shown to be well formed.
/// </remarks>
internal static class AuthoringRequestValidation
{
    /// <summary>
    /// Resolves the route's content type and checks the key's shape.
    /// </summary>
    /// <remarks>
    /// The same gate the read path applies, and for the same reason: the type
    /// must become a member of the compiled registry before any store sees it,
    /// and the key must match the slug pattern before it reaches a query or a
    /// path join. A key that cannot exist is a 400 rather than a 404 — the
    /// request itself is malformed, and answering 404 would invite a client to
    /// retry it.
    /// </remarks>
    public static bool TryResolve(
        string type,
        string key,
        out ContentTypeDefinition? definition,
        out ProblemHttpResult? problem)
    {
        if (!ContentTypeRegistry.TryResolve(type, out var resolved))
        {
            definition = null;
            problem = AuthoringProblems.UnknownType;
            return false;
        }

        if (!ContentSlug.IsValid(key))
        {
            definition = null;
            problem = AuthoringProblems.Invalid(
                "key",
                "A content key is lowercase letters, digits and single hyphens, " +
                $"and at most {ContentSlug.MaxLength} characters.");
            return false;
        }

        definition = resolved;
        problem = null;
        return true;
    }

    /// <summary>Checks the proposed document is an object of a sane size.</summary>
    /// <remarks>
    /// Size is measured on the parsed document rather than on
    /// <c>Content-Length</c>, because the request is already bound by the time a
    /// handler runs — the framework's own request-body limit is what stops an
    /// unbounded upload before that. This check exists to stop a document that
    /// is within the transport limit but far larger than any real content item
    /// from being validated, snapshotted into a revision and stored.
    /// </remarks>
    public static bool TryReadDocument(JsonElement document, out ProblemHttpResult? problem)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            problem = AuthoringProblems.Invalid(
                "document",
                "A content document must be a JSON object.");
            return false;
        }

        var size = Encoding.UTF8.GetByteCount(document.GetRawText());

        if (size > ContentAuthoringLimits.MaxDocumentBytes)
        {
            problem = AuthoringProblems.Invalid(
                "document",
                $"A content document may be at most {ContentAuthoringLimits.MaxDocumentBytes} bytes.");
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Bounds the actor's note.</summary>
    /// <remarks>
    /// Refused rather than truncated. Silently cutting somebody's explanation in
    /// half produces an audit record that reads as though they stopped
    /// mid-sentence, and the one thing a reason has to be is what the person
    /// actually wrote.
    /// </remarks>
    public static bool TryReadReason(
        string? reason,
        out string? value,
        out ProblemHttpResult? problem)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            value = null;
            problem = null;
            return true;
        }

        if (reason.Length > ContentAuthoringLimits.MaxReasonLength)
        {
            value = null;
            problem = AuthoringProblems.Invalid(
                "reason",
                $"A reason may be at most {ContentAuthoringLimits.MaxReasonLength} characters.");
            return false;
        }

        // Control characters and bidirectional overrides are refused, matching
        // the rule the moderation free text already applies: a note is rendered
        // to other reviewers, and an override character can make it display as
        // something other than what is stored.
        foreach (var character in reason)
        {
            if (char.IsControl(character) && character is not ('\n' or '\r' or '\t'))
            {
                value = null;
                problem = AuthoringProblems.Invalid(
                    "reason", "A reason may not contain control characters.");
                return false;
            }

            if (character is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
            {
                value = null;
                problem = AuthoringProblems.Invalid(
                    "reason", "A reason may not contain bidirectional override characters.");
                return false;
            }
        }

        value = reason;
        problem = null;
        return true;
    }

    /// <summary>Bounds how much history one request may ask for.</summary>
    public static bool TryReadLimit(
        int? limit,
        out int value,
        [NotNullWhen(false)] out ProblemHttpResult? problem)
    {
        if (limit is null)
        {
            value = ContentAuthoringLimits.DefaultRevisionPageSize;
            problem = null;
            return true;
        }

        if (limit < 1 || limit > ContentAuthoringLimits.MaxRevisionPageSize)
        {
            value = 0;
            problem = AuthoringProblems.Invalid(
                "limit",
                $"A history request may ask for between 1 and " +
                $"{ContentAuthoringLimits.MaxRevisionPageSize} revisions.");
            return false;
        }

        value = limit.Value;
        problem = null;
        return true;
    }
}
