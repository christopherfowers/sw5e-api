using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sw5e.Email.Providers.MailerSend;

/// <summary>
/// The JSON body of <c>POST /v1/email</c>, exactly as MailerSend documents it.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated set of types rather than an anonymous object, so that the wire
/// contract is a thing a reader can look at and a test can assert against. The
/// property names are snake_case because MailerSend's are; they are spelled out
/// with <see cref="JsonPropertyNameAttribute"/> rather than left to a naming
/// policy, because a policy is a rule that has to be remembered and an
/// attribute is a fact that is visible at the point of use.
/// </para>
/// <para>
/// Only the fields this subsystem sends are modelled. MailerSend's endpoint
/// also accepts <c>cc</c>, <c>bcc</c>, <c>attachments</c>, <c>template_id</c>,
/// <c>personalization</c>, <c>tags</c>, <c>send_at</c> and more; every one of
/// them is either unrepresentable in <see cref="EmailMessage"/> or deliberately
/// excluded there, and adding one here without adding it to the other adapters
/// would be the first crack in the abstraction.
/// </para>
/// </remarks>
internal sealed record MailerSendPayload
{
    /// <summary>
    /// The sender. Must be on a domain verified in the MailerSend account, or
    /// the API answers 422 no matter how well-formed the address is.
    /// </summary>
    [JsonPropertyName("from")]
    public required MailerSendContact From { get; init; }

    /// <summary>
    /// Recipients. An array because the API requires one; it always holds
    /// exactly one entry, because <see cref="EmailMessage.To"/> is singular.
    /// </summary>
    [JsonPropertyName("to")]
    public required IReadOnlyList<MailerSendContact> To { get; init; }

    /// <summary>The subject line. MailerSend caps this at 998 characters.</summary>
    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    /// <summary>The <c>text/plain</c> alternative.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>The <c>text/html</c> alternative.</summary>
    [JsonPropertyName("html")]
    public required string Html { get; init; }

    /// <summary>
    /// Optional reply-to mailbox. Omitted from the JSON entirely when unset —
    /// see <see cref="MailerSendSerialization.Options"/> for why null is not
    /// sent instead.
    /// </summary>
    [JsonPropertyName("reply_to")]
    public MailerSendContact? ReplyTo { get; init; }
}

/// <summary>A mailbox as MailerSend represents one.</summary>
/// <param name="Email">The address.</param>
/// <param name="Name">The display name, omitted when there is none.</param>
internal sealed record MailerSendContact(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>Serialisation settings shared by the adapter and its tests.</summary>
internal static class MailerSendSerialization
{
    /// <summary>
    /// The options used for every request body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nulls are dropped. MailerSend validates the shape of what it is given,
    /// and an explicit <c>"reply_to": null</c> is not the same as an absent
    /// key — the first invites a 422 for a field that was never wanted. The
    /// same applies to a contact's <c>name</c>.
    /// </para>
    /// <para>
    /// Encoding is left at the strict default. The bodies contain HTML, so
    /// <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> arrive escaped as <c><</c>
    /// and friends. That is valid JSON, MailerSend unescapes it, and it costs a
    /// few bytes — which is a good trade for not having to reason about what a
    /// relaxed encoder would do with a body that is markup by construction.
    /// </para>
    /// <para>
    /// A single cached instance because <see cref="JsonSerializerOptions"/>
    /// builds and caches its metadata on first use; a fresh one per send throws
    /// that away every time.
    /// </para>
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
