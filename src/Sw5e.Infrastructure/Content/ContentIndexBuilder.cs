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

        if (!TryReadString(body, "key", out var key) || !string.Equals(key, fileKey, StringComparison.Ordinal))
        {
            warnings.Add($"Skipped '{file}': the 'key' property must be present and equal to the file name.");
            return null;
        }

        var nameField = ContentProjection.NameField(definition.Key);

        if (!TryReadString(body, nameField, out var name))
        {
            warnings.Add($"Skipped '{file}': required property '{nameField}' is missing or empty.");
            return null;
        }

        // Absent rather than empty: "feature" documents carry neither of these,
        // and the difference has to survive to the sort, where a null orders
        // last instead of first.
        var sourceKey = ReadStringOrNull(body, "sourceKey");
        var contentSet = ReadStringOrNull(body, "contentSet");

        var searchText = ContentProjection.SearchText(body);

        return new IndexedContentItem
        {
            Type = definition,
            Key = key,
            Name = name,
            Version = ComputeVersion(bytes),
            Body = body,
            SourceKey = sourceKey,
            ContentSet = contentSet,
            Summary = ContentProjection.Summary(definition.Key, body),
            Facets = ContentProjection.Facets(definition.Key, body),
            SearchText = searchText,
            NameLower = name.ToLowerInvariant(),
            SearchTextLower = searchText.ToLowerInvariant(),
        };
    }

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
    private static string ComputeVersion(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

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
