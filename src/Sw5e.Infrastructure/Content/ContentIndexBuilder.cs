using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sw5e.Domain.Content;

namespace Sw5e.Infrastructure.Content;

/// <summary>
/// Scans a content directory once and produces the in-memory index the
/// filesystem store serves from.
/// </summary>
/// <remarks>
/// <para>
/// This is the only code in the API that touches the content directory, and it
/// runs at startup rather than per request. That is deliberate: a request never
/// causes a path to be built from a route value, so the directory-traversal
/// class of bug has no reachable path here at all. The containment check in
/// <see cref="ResolveTypeDirectory"/> is a second line, guarding against a
/// future caller that forgets.
/// </para>
/// <para>
/// A missing directory, an unreadable file, a file that is not JSON, and a file
/// whose contents do not match its type's schema are all survivable: each is
/// reported as a warning and skipped. The content repository is populated by a
/// separate project on its own schedule, so an API that refused to start
/// against a half-populated directory would be unusable for as long as that
/// work took.
/// </para>
/// </remarks>
internal static class ContentIndexBuilder
{
    /// <summary>
    /// Outcome of one scan: the index to serve, plus anything an operator
    /// should see in the log.
    /// </summary>
    /// <param name="Items">
    /// Every item the scan loaded, in scan order. Exposed alongside the index
    /// because the database importer reads the same directory and needs the
    /// same items: what counts as a valid content file, how its display name is
    /// found, and how its row and search text are projected are decisions that
    /// have to be made identically by both stores, and the only way to
    /// guarantee that is for both to come through this one scan.
    /// </param>
    /// <param name="ItemCount">Total items indexed across all types.</param>
    /// <param name="Warnings">
    /// Human-readable notes about skipped files. These are logged at startup
    /// and never reach a response, because they name filesystem paths.
    /// </param>
    internal sealed record Result(
        ContentIndex Index,
        IReadOnlyList<IndexedContentItem> Items,
        int ItemCount,
        IReadOnlyList<string> Warnings);

    /// <summary>Builds an index from <paramref name="rootPath"/>.</summary>
    /// <param name="rootPath">
    /// Absolute or relative path to the directory holding one subdirectory per
    /// content type. Need not exist.
    /// </param>
    internal static Result Build(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.GetFullPath(rootPath);
        var warnings = new List<string>();

        if (!Directory.Exists(root))
        {
            warnings.Add(
                $"Content directory '{root}' does not exist; serving an empty catalogue.");

            return new Result(ContentIndex.Empty, [], 0, warnings);
        }

        var items = new List<IndexedContentItem>();

        foreach (var definition in ContentTypeRegistry.All)
        {
            var directory = ResolveTypeDirectory(root, definition);

            if (directory is null || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var item = TryLoad(definition, file, warnings);

                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        return new Result(
            ContentIndex.Create(items, ComputeIndexVersion(items)),
            items,
            items.Count,
            warnings);
    }

    /// <summary>
    /// Joins the content root to a type's directory and confirms the result is
    /// genuinely inside the root.
    /// </summary>
    /// <remarks>
    /// The type key is a compile-time constant from
    /// <see cref="ContentTypeRegistry"/>, so today this can only succeed. The
    /// check stays because the same shape of code, fed a route value instead,
    /// is a directory-traversal vulnerability, and the cheapest way to keep it
    /// from being reintroduced is for the safe version to be the one already
    /// written.
    /// </remarks>
    private static string? ResolveTypeDirectory(string root, ContentTypeDefinition definition)
    {
        if (!ContentSlug.IsValid(definition.Key))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, definition.Key));
        var relative = Path.GetRelativePath(root, candidate);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return candidate;
    }

    private static IndexedContentItem? TryLoad(
        ContentTypeDefinition definition,
        string file,
        List<string> warnings)
    {
        var fileKey = Path.GetFileNameWithoutExtension(file);

        if (!ContentSlug.IsValid(fileKey))
        {
            warnings.Add($"Skipped '{file}': file name is not a valid content key.");
            return null;
        }

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (IOException exception)
        {
            warnings.Add($"Skipped '{file}': {exception.Message}");
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            warnings.Add($"Skipped '{file}': {exception.Message}");
            return null;
        }

        JsonElement body;

        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"Skipped '{file}': root value is not a JSON object.");
                return null;
            }

            // Clone detaches the element from the document's pooled buffers so
            // it can outlive this using block.
            body = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            warnings.Add($"Skipped '{file}': {exception.Message}");
            return null;
        }

        var projected = TryProject(definition, fileKey, body, ComputeVersion(bytes), out var failure);

        if (projected is null)
        {
            warnings.Add($"Skipped '{file}': {failure}");
            return null;
        }

        return projected;
    }

    /// <summary>
    /// Derives the projected row from a document already in memory.
    /// </summary>
    /// <param name="definition">The resolved content type.</param>
    /// <param name="expectedKey">
    /// The key the document is filed under — the file name for the scanner, the
    /// route value for an authored write. The document's own <c>key</c>
    /// property must agree with it.
    /// </param>
    /// <param name="body">The document. Must be a JSON object.</param>
    /// <param name="version">
    /// The opaque change token for this document. Supplied by the caller
    /// because the two callers derive it from different bytes: the scanner
    /// hashes the file as it sits on disk, and the authoring store hashes the
    /// document it is about to store.
    /// </param>
    /// <param name="failure">Why the document was rejected, when it was.</param>
    /// <returns>The projection, or null when the document cannot be filed.</returns>
    /// <remarks>
    /// Shared by the filesystem scan and the authoring store rather than
    /// restated in each. The projected columns are what every list, sort,
    /// filter and search reads, and the two stores are held to parity on all of
    /// them by an explicit test suite — so a document that arrives through an
    /// endpoint has to be projected by the identical code that projects one
    /// arriving as a file, or the parity that suite asserts becomes a property
    /// only of content that came from disk.
    /// </remarks>
    internal static IndexedContentItem? TryProject(
        ContentTypeDefinition definition,
        string expectedKey,
        JsonElement body,
        string version,
        out string? failure)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            failure = "root value is not a JSON object.";
            return null;
        }

        if (!TryReadString(body, "key", out var key) ||
            !string.Equals(key, expectedKey, StringComparison.Ordinal))
        {
            failure = "the 'key' property must be present and equal to the item key.";
            return null;
        }

        var nameField = ContentProjection.NameField(definition.Key);

        if (!TryReadString(body, nameField, out var name))
        {
            failure = $"required property '{nameField}' is missing or empty.";
            return null;
        }

        // Absent rather than empty: "feature" documents carry neither of these,
        // and the difference has to survive to the sort, where a null orders
        // last instead of first.
        var sourceKey = ReadStringOrNull(body, "sourceKey");
        var contentSet = ReadStringOrNull(body, "contentSet");

        var searchText = ContentProjection.SearchText(body);
        var headingText = ContentProjection.HeadingText(body);

        failure = null;

        return new IndexedContentItem
        {
            Type = definition,
            Key = key,
            Name = name,
            Version = version,
            Body = body,
            SourceKey = sourceKey,
            ContentSet = contentSet,
            Summary = ContentProjection.Summary(definition.Key, body),
            Facets = ContentProjection.Facets(definition.Key, body),
            SearchText = searchText,
            NameLower = name.ToLowerInvariant(),
            SearchTextLower = searchText.ToLowerInvariant(),
            HeadingTextLower = headingText.ToLowerInvariant(),
        };
    }

    /// <summary>
    /// The change token for a document that never existed as a file.
    /// </summary>
    /// <remarks>
    /// Same construction as the scanner's — a truncated SHA-256, so the two are
    /// the same shape and the same width — but over the document as it will be
    /// stored rather than over file bytes. The token's contract is only that it
    /// changes when the document changes and does not when it has not, and
    /// hashing the stored text satisfies both. It deliberately does not try to
    /// predict the hash the scanner would compute if this document were later
    /// written out to a file: that depends on the exporter's formatting, and a
    /// token that quietly depended on formatting would be a token that changed
    /// when nothing had.
    /// </remarks>
    internal static string ComputeVersionFor(JsonElement body) =>
        Hash(System.Text.Encoding.UTF8.GetBytes(body.GetRawText()));

    private static string? ReadStringOrNull(JsonElement body, string property) =>
        TryReadString(body, property, out var value) ? value : null;

    private static bool TryReadString(JsonElement body, string property, out string value)
    {
        if (body.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// A content hash of one file's bytes. Any edit changes it, which is
    /// exactly what an ETag needs; a timestamp would not survive a redeploy
    /// that rewrites files unchanged.
    /// </summary>
    private static string ComputeVersion(byte[] bytes) => Hash(bytes);

    /// <summary>
    /// The version of a document, over its bytes and over the projection that
    /// turns those bytes into a row.
    /// </summary>
    /// <remarks>
    /// The projection is part of it because the importer decides whether to
    /// rewrite a row by comparing this value, and a document that is projected
    /// differently produces a different row from identical bytes. Hashing the
    /// bytes alone made that change invisible: see
    /// <see cref="ContentProjection.Version"/> for what it cost.
    /// </remarks>
    private static string Hash(ReadOnlySpan<byte> bytes)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Length-prefixed rather than simply concatenated, so that a projection
        // version cannot be confused with the first bytes of a document.
        digest.AppendData(System.Text.Encoding.UTF8.GetBytes(
            $"{ContentProjection.Version.Length}:{ContentProjection.Version}"));
        digest.AppendData(bytes);

        return Convert.ToHexStringLower(digest.GetCurrentHash())[..16];
    }

    private static string ComputeIndexVersion(List<IndexedContentItem> items)
    {
        if (items.Count == 0)
        {
            return "empty";
        }

        var builder = new StringBuilder();

        foreach (var item in items.OrderBy(i => i.Type.Key, StringComparer.Ordinal)
                                  .ThenBy(i => i.Key, StringComparer.Ordinal))
        {
            builder.Append(item.Type.Key).Append('/').Append(item.Key).Append(':')
                   .Append(item.Version).Append(';');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }
}
