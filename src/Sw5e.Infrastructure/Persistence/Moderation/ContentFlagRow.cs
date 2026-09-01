using Sw5e.Domain.Moderation;

namespace Sw5e.Infrastructure.Persistence.Moderation;

/// <summary>
/// One report, as stored.
/// </summary>
/// <remarks>
/// <para>
/// A flag is a record <em>about</em> a content document and never a change
/// <em>to</em> one. Nothing in this row is read while rendering the reference,
/// nothing the reference serves is written when a flag is raised, and there is
/// no code path from here into the content tables. That is the property that
/// let this ship before there is any content authoring at all: reports can
/// accumulate for months against a catalogue that is still read-only, and the
/// day editing arrives they are the worklist it starts from.
/// </para>
/// <para>
/// The consequence to keep in mind is that a flag can outlive what it points
/// at. A re-import can retire a document; the flags against it stay, and the
/// queue shows them as pointing at something that is no longer published rather
/// than vanishing. Losing the report would lose the only record that somebody
/// noticed.
/// </para>
/// </remarks>
public sealed class ContentFlagRow
{
    /// <summary>
    /// Surrogate key, and deliberately a <see cref="Guid"/>.
    /// </summary>
    /// <remarks>
    /// Flag identifiers appear in URLs that a signed-in account addresses. A
    /// sequential integer would publish how many reports the platform has ever
    /// received and let anybody holding one identifier guess its neighbours —
    /// which, on a queue that will carry rights complaints and the names of the
    /// people who raised them, is a disclosure rather than a curiosity.
    /// </remarks>
    public Guid Id { get; set; }

    /// <summary>Whether this is about a picture or about a document.</summary>
    /// <remarks>
    /// Stored rather than derived from <see cref="Reason"/>, even though the
    /// endpoint derives it on the way in. The queue filters and groups on it,
    /// and a filter that has to evaluate a C# expression over ten reason values
    /// is a filter PostgreSQL cannot index.
    /// </remarks>
    public FlagTargetKind TargetKind { get; set; }

    /// <summary>The content type key: <c>species</c>, <c>power</c>, <c>asset-credit</c>.</summary>
    /// <remarks>
    /// A plain column with no foreign key to <c>content.content_type</c>, and
    /// that is deliberate: the two schemas are separate migration streams that
    /// a deployment may eventually put on separate databases, and a constraint
    /// across them would quietly make that impossible. What actually stops a
    /// nonsense value getting here is the endpoint, which resolves the caller's
    /// string against the compiled registry and stores the registry's instance.
    /// </remarks>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>The document key within its type.</summary>
    public string TargetKey { get; set; } = string.Empty;

    /// <summary>
    /// The document's name at the moment the flag was raised.
    /// </summary>
    /// <remarks>
    /// Copied rather than joined, which is a denormalisation and is the right
    /// one. The queue has to be readable without a query against the content
    /// store — the two may not share a database — and, more importantly, a
    /// report about a document that has since been retired or renamed must
    /// still say what the reporter was looking at. A join would render it as a
    /// bare key or as the new name, and in both cases the reviewer loses the
    /// context the report depended on.
    /// </remarks>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>Why it was raised.</summary>
    public FlagReason Reason { get; set; }

    /// <summary>
    /// What the reporter wrote, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is hostile input and it is stored verbatim.</b> It is not
    /// stripped of markup, not HTML-encoded on the way in, and not run through
    /// any sanitiser. Encoding at rest is the classic mistake: it makes the
    /// column's contents depend on which writer inserted them, double-encodes
    /// the moment anything re-encodes on output, and it mangles the perfectly
    /// ordinary sentences this field exists to collect — an artist writing
    /// <c>the &lt;Twi'lek&gt; portrait is mine</c> deserves to have said that.
    /// </para>
    /// <para>
    /// Safety comes from escaping at every point of output instead, which is
    /// the only place that knows what it is escaping <em>for</em>. The API's
    /// JSON writer escapes it for JSON; the browser client renders it as a text
    /// node and never as markup. The one rule that must hold forever is that
    /// nothing interpolates this value into HTML, and nothing feeds it to the
    /// site's Markdown renderer.
    /// </para>
    /// <para>
    /// Bounded at <see cref="ContentFlagRules.MaxDetailsLength"/>, and the
    /// bound is in the column as well as in the validator: a validator is a
    /// check, a column length is a constraint, and only one of them survives
    /// somebody writing a second insert path.
    /// </para>
    /// </remarks>
    public string? Details { get; set; }

    /// <summary>Where the report has got to.</summary>
    public FlagStatus Status { get; set; }

    /// <summary>
    /// The account that raised it.
    /// </summary>
    /// <remarks>
    /// No foreign key to <c>identity."AspNetUsers"</c>, for the same reason
    /// <see cref="TargetType"/> has none: identity resolves its own connection
    /// string and may live in a database of its own. The queue resolves display
    /// names by asking the identity store for the accounts it needs, in one
    /// query per page, and renders "a removed account" for an identifier that
    /// no longer matches one — which is also what a deleted account should look
    /// like on a report that outlived it.
    /// </remarks>
    public Guid ReporterUserId { get; set; }

    /// <summary>When it was raised, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The account that last moved this flag through the lifecycle, or null
    /// while it is untouched.
    /// </summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>When that happened, in UTC. Null while nobody has acted.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>
    /// What the reviewer said about their decision, or null.
    /// </summary>
    /// <remarks>
    /// Written by a Contributor or an Administrator rather than by the public,
    /// which makes it less hostile and not trustworthy: an account can be
    /// compromised, and a note is rendered to other reviewers. It is treated
    /// exactly like <see cref="Details"/> — bounded, stored verbatim, escaped
    /// on output.
    /// </remarks>
    public string? ReviewerNote { get; set; }

    /// <summary>
    /// The content revision that put the reported problem right, when one did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what closes the loop the review queue opened. Until now a
    /// reviewer could accept a report and then had nowhere to go: agreeing that
    /// a picture is uncredited and recording that it has been credited were the
    /// same button, backed by nothing. With authoring in place, resolving a
    /// report can name the revision that did it, so "this was fixed" becomes a
    /// claim someone can follow to a diff rather than an assertion in a note.
    /// </para>
    /// <para>
    /// Deliberately not a foreign key, and deliberately a bare identifier
    /// rather than a navigation. Moderation is its own schema and may be its own
    /// database — the whole reason it does not carry an FK to the reporter's
    /// account either — so a constraint reaching into the content schema would
    /// either not exist or would weld the two together. The store checks the
    /// revision is real before writing it; nothing downstream assumes it still
    /// is.
    /// </para>
    /// </remarks>
    public long? ResolvedByRevisionId { get; set; }
}
