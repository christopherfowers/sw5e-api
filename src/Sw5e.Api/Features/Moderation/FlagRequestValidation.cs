using Microsoft.AspNetCore.Http.HttpResults;
using Sw5e.Domain.Content;
using Sw5e.Domain.Moderation;

namespace Sw5e.Api.Features.Moderation;

/// <summary>
/// Everything a flag request has to satisfy before anything is stored.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the handler so the order is visible in one place, because the
/// order is part of the design. Cheap checks that need nothing outside the
/// request run before the one that costs a read of the content store, so a
/// request that names a nonexistent reason never gets to spend a query proving
/// its target exists.
/// </para>
/// <para>
/// Nothing here sanitises. Free text is bounded and rejected, never rewritten:
/// the value stored is the value sent, and safety comes from escaping wherever
/// it is rendered. Silently editing what somebody wrote would also make the
/// stored text a poor record of what they actually reported, which matters most
/// for the one report that turns out to be a rights complaint.
/// </para>
/// </remarks>
internal static class FlagRequestValidation
{
    /// <summary>Resolves the reason, or explains why it could not be.</summary>
    public static bool TryReadReason(
        string? value,
        out FlagReason reason,
        out ProblemHttpResult? problem)
    {
        if (FlagWire.TryParseReason(value, out reason))
        {
            problem = null;
            return true;
        }

        problem = FlagProblems.Invalid(
            "reason",
            "That is not a reason this site accepts. It must be one of: " +
            string.Join(", ", FlagWire.ReasonNames) + ".");

        return false;
    }

    /// <summary>Resolves the target, or explains why it could not be.</summary>
    /// <remarks>
    /// <para>
    /// The registry decides, and what is carried forward is the registry's own
    /// instance rather than the caller's string — the same rule the content
    /// endpoints follow, and for the same reason: this value ends up in a
    /// filesystem path join in one store and a table selection in the other.
    /// </para>
    /// <para>
    /// A type this site does not serve is a 400 rather than a 404. The registry
    /// is published in full at <c>/api/content-types</c>, so refusing to name
    /// the problem would hide nothing and would leave a client unable to tell a
    /// typo in the type from a typo in the key.
    /// </para>
    /// </remarks>
    public static bool TryReadTarget(
        string? type,
        string? key,
        out ContentTypeDefinition definition,
        out string targetKey,
        out ProblemHttpResult? problem)
    {
        definition = null!;
        targetKey = string.Empty;

        if (!ContentTypeRegistry.TryResolve(type, out var resolved))
        {
            problem = FlagProblems.Invalid(
                "targetType",
                "That is not a content type this site serves.");

            return false;
        }

        if (!ContentSlug.IsValid(key))
        {
            problem = FlagProblems.Invalid(
                "targetKey",
                "A content key is lowercase letters and digits in hyphen-separated groups.");

            return false;
        }

        definition = resolved;
        targetKey = key!;
        problem = null;
        return true;
    }

    /// <summary>
    /// Decides whether the reason and the target are talking about the same
    /// kind of thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every picture the site draws has an <c>asset-credit</c> document, so
    /// "this is about a picture" and "this points at an asset-credit record"
    /// are the same statement. That makes the check a single comparison rather
    /// than a second field the caller has to get right, and it makes the
    /// mismatch — <c>image-artist-known</c> raised against a rules chapter —
    /// something the server refuses rather than something the queue has to
    /// display.
    /// </para>
    /// <para>
    /// <c>other</c> is the one reason that belongs to both kinds, so it takes
    /// the kind from the target instead of imposing one.
    /// </para>
    /// </remarks>
    public static bool TryReadKind(
        FlagReason reason,
        ContentTypeDefinition definition,
        out FlagTargetKind kind,
        out ProblemHttpResult? problem)
    {
        var pointsAtAPicture = string.Equals(
            definition.Key,
            ContentFlagRules.ImageContentType,
            StringComparison.Ordinal);

        kind = pointsAtAPicture ? FlagTargetKind.Image : FlagTargetKind.Document;

        if (ContentFlagRules.Permits(reason, kind))
        {
            problem = null;
            return true;
        }

        problem = FlagProblems.Invalid(
            "reason",
            pointsAtAPicture
                ? "That reason is about writing, and this report is about a picture."
                : "That reason is about a picture. Report a picture through its attribution " +
                  "record, which is what identifies it.");

        return false;
    }

    /// <summary>
    /// Reads the free-text explanation: trimmed, bounded, and refused rather
    /// than rewritten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three rules, and each one is here because of what happens without it.
    /// </para>
    /// <para>
    /// <b>A length limit</b>, because this column is written from a request an
    /// ordinary account controls and read back by moderators. Unbounded, it is
    /// free storage for anybody with a session and a page that takes a second
    /// to render for the people who have to work the queue.
    /// </para>
    /// <para>
    /// <b>No control characters</b> beyond tab and newline. They are invisible
    /// in every interface a reviewer will use, which makes them the natural
    /// material for a report whose rendered text says one thing and whose
    /// stored text says another — and a bidirectional override in a
    /// moderator's queue can reverse the meaning of a sentence they are about
    /// to act on. Carriage returns are folded into newlines first, because a
    /// browser sends <c>\r\n</c> from a textarea and rejecting the entire
    /// report over it would be absurd.
    /// </para>
    /// <para>
    /// <b>Compulsory for <c>other</c></b>, because "other" with nothing after
    /// it is a report that cannot be acted on, and the queue's whole value is
    /// that every row in it is actionable.
    /// </para>
    /// <para>
    /// What is deliberately <em>not</em> here is any attempt to strip markup.
    /// See <c>ContentFlagRow.Details</c>.
    /// </para>
    /// </remarks>
    public static bool TryReadText(
        string? value,
        string field,
        int maxLength,
        bool required,
        out string? details,
        out ProblemHttpResult? problem)
    {
        details = null;

        var text = value?.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Replace('\r', '\n')
                         .Trim();

        if (string.IsNullOrEmpty(text))
        {
            if (required)
            {
                problem = FlagProblems.Invalid(
                    field,
                    "Tell us what the problem is. A report of \"other\" with nothing written " +
                    "under it is one nobody can act on.");

                return false;
            }

            problem = null;
            return true;
        }

        if (text.Length > maxLength)
        {
            problem = FlagProblems.Invalid(
                field,
                $"Keep this under {maxLength} characters. It was {text.Length}.");

            return false;
        }

        foreach (var character in text)
        {
            if (IsRefusedCharacter(character))
            {
                problem = FlagProblems.Invalid(
                    field,
                    "That text contains characters that cannot be displayed. Remove them and " +
                    "try again.");

                return false;
            }
        }

        details = text;
        problem = null;
        return true;
    }

    /// <summary>
    /// Whether a character is one this field refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two families, and the list is narrow on purpose. C0 and C1 control
    /// characters other than tab and newline, because they are invisible and
    /// have no meaning in a sentence. And the bidirectional overrides and
    /// isolates, because they are the Trojan Source characters: they reorder
    /// how a line renders without changing its bytes, so a report can be made
    /// to read one way in a moderator's queue and mean another.
    /// </para>
    /// <para>
    /// What is deliberately <em>not</em> refused is the rest of the format
    /// category. A blanket ban on Cf is the tempting one-liner and it would
    /// reject zero-width non-joiners, which Persian and Hindi need in order to
    /// spell ordinary words. A site inherited from a community that spans the
    /// world should not refuse a report because of the language it is in.
    /// </para>
    /// </remarks>
    private static bool IsRefusedCharacter(char character)
    {
        if (character is '\n' or '\t')
        {
            return false;
        }

        return char.IsControl(character) || character is
            // Left-to-right and right-to-left embedding and override, and the
            // pop that closes them.
            >= '\u202A' and <= '\u202E' or
            // The four directional isolates, which do the same job with
            // cleaner nesting and are just as invisible.
            >= '\u2066' and <= '\u2069';
    }

    /// <summary>Reads the status a reviewer is asking for.</summary>
    public static bool TryReadStatus(
        string? value,
        out FlagStatus status,
        out ProblemHttpResult? problem)
    {
        if (FlagWire.TryParseStatus(value, out status))
        {
            problem = null;
            return true;
        }

        problem = FlagProblems.Invalid(
            "status",
            "That is not a status. It must be one of: " +
            string.Join(", ", FlagWire.StatusNames) + ".");

        return false;
    }
}
