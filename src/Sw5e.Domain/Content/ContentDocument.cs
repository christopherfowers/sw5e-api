using System.Text.Json;

namespace Sw5e.Domain.Content;

/// <summary>
/// One content item in full.
/// </summary>
/// <param name="Type">Canonical content type key.</param>
/// <param name="Key">Slug identifying the item within its type.</param>
/// <param name="Name">Display name, lifted out of the body for convenience.</param>
/// <param name="Version">
/// Opaque token that changes whenever the item's body changes. The store
/// computes it — a content hash for the filesystem store, a row version or an
/// <c>md5(body)</c> for the database one — so the API can emit an ETag without
/// knowing how either store detects change.
/// </param>
/// <param name="Body">
/// The item exactly as it validates against its JSON Schema. Passed through
/// verbatim so the response shape is the schema, with no hand-maintained DTO
/// per type to drift away from it.
/// </param>
public sealed record ContentDocument(
    string Type,
    string Key,
    string Name,
    string Version,
    JsonElement Body);
