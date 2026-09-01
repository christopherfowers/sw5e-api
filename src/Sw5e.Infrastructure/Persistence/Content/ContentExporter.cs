using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sw5e.Database.Schemas;
using Sw5e.Domain.Content;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// What one document's export did, and why.
/// </summary>
public enum ContentExportOutcome
{
    /// <summary>The file on disk already held exactly what the database holds.</summary>
    Unchanged,

    /// <summary>The document differs from the file, which was rewritten.</summary>
    Changed,

    /// <summary>The document is in the database and no file existed for it.</summary>
    Added,

    /// <summary>A file exists that the database no longer publishes.</summary>
    Removed,
}

/// <summary>One document the export wrote, or would have written.</summary>
/// <param name="ContentType">The type key, which is also the directory name.</param>
/// <param name="Key">The document's slug within its type.</param>
/// <param name="Outcome">What the export did to it.</param>
public readonly record struct ContentExportChange(
    string ContentType,
    string Key,
    ContentExportOutcome Outcome)
{
    /// <summary>The path relative to the content root, as a reviewer sees it.</summary>
    public string Path => $"{ContentType}/{Key}.json";

    public override string ToString() => Outcome switch
    {
        ContentExportOutcome.Added => $"{Path}: in the database, not in the repository",
        ContentExportOutcome.Removed => $"{Path}: in the repository, not in the database",
        ContentExportOutcome.Changed => $"{Path}: differs",
        _ => Path,
    };
}

/// <summary>What one export run did.</summary>
/// <param name="Examined">Documents read out of the database.</param>
/// <param name="Unchanged">Documents whose file already matched.</param>
/// <param name="Changes">
/// Every document that was written, added or removed — in other words, every
/// way the database and the repository disagreed.
/// </param>
/// <param name="Warnings">
/// Diagnostics an operator should see. These name filesystem paths and are for
/// a log, not a response.
/// </param>
public sealed record ContentExportResult(
    int Examined,
    int Unchanged,
    IReadOnlyList<ContentExportChange> Changes,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Whether the database and the repository agreed about everything.</summary>
    public bool InAgreement => Changes.Count == 0;

    public int Added => Changes.Count(change => change.Outcome is ContentExportOutcome.Added);

    public int Changed => Changes.Count(change => change.Outcome is ContentExportOutcome.Changed);

    public int Removed => Changes.Count(change => change.Outcome is ContentExportOutcome.Removed);
}

/// <summary>What to export, and where.</summary>
/// <param name="ContentRoot">
/// The <c>content/</c> directory to write, normally inside a checkout of the
/// content repository. Created if it does not exist.
/// </param>
/// <param name="ContentType">
/// Restrict the export to one type. Null exports every type.
/// </param>
/// <param name="Key">
/// Restrict the export to one document within <paramref name="ContentType"/>,
/// which must then be given. Null exports every document of the type.
/// </param>
/// <param name="Prune">
/// Whether to delete files the database no longer publishes. Ignored — and
/// refused — when <paramref name="Key"/> narrows the run to one document, since
/// a single-document export has no opinion about any other file.
/// </param>
/// <param name="CheckOnly">
/// Report the differences and write nothing. This is what an operator runs to
/// answer "has anything been published since the last export?" without
/// producing a diff they then have to throw away.
/// </param>
public sealed record ContentExportRequest(
    string ContentRoot,
    string? ContentType = null,
    string? Key = null,
    bool Prune = true,
    bool CheckOnly = false);

/// <summary>
/// Writes the published catalogue back out as the content repository holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Content used to move in one direction: a pull request
/// against the content repository, an image, a deploy, and the importer loaded
/// it into PostgreSQL. Authoring reversed that for everything edited through the
/// site — those documents exist only as rows, and the repository, which is still
/// the seed and still what the published content image carries, drifts away from
/// them silently. The next person to rebuild that image reverts the community's
/// work without any step in the process saying so. This is the other direction.
/// </para>
/// <para>
/// <b>What "published" means here.</b> The catalogue table, and nothing else. A
/// draft is a row in a different table precisely so that it is not part of the
/// catalogue, so excluding drafts is not a filter this applies — it is a
/// consequence of reading the same rows the read path serves. A revert is a
/// write to the catalogue like any other, so a reverted document exports as
/// whatever it was reverted to. Neither needed a special case, and that is the
/// argument for reading the catalogue rather than replaying the revision log.
/// </para>
/// <para>
/// <b>Why it does not commit.</b> It produces a working tree and stops. Writing
/// files needs a path; committing needs an identity to attribute the commit to,
/// and pushing needs a credential with write access to the content repository
/// held by a process that also holds the whole catalogue. That is a meaningfully
/// larger thing to get wrong, and it buys nothing that a scheduled job running
/// <c>git commit</c> next to this one does not: the review still happens in a
/// pull request either way. So the boundary is the tree.
/// </para>
/// <para>
/// <b>Why every document is validated on the way out.</b> The importer does not
/// validate against the JSON Schemas — it loads whatever the corpus holds — and
/// a row can also be written by a migration or by hand. The content repository's
/// CI validates every document on every pull request, so an export that emitted
/// something the schema rejects would produce a branch that cannot be merged,
/// discovered by whoever opened the pull request rather than by whoever ran the
/// export. Refusing to write is louder and lands on the right person.
/// </para>
/// </remarks>
public sealed class ContentExporter(
    Sw5eContentDbContext database,
    CanonicalContent canonical,
    IContentSchemaValidator validator,
    ILogger<ContentExporter> logger)
{
    /// <summary>Exports the published catalogue into a content tree.</summary>
    /// <exception cref="ArgumentException">The request is not coherent.</exception>
    /// <exception cref="InvalidOperationException">
    /// A document in the database does not satisfy its own schema, so the tree
    /// this would have produced could not be merged.
    /// </exception>
    public async Task<ContentExportResult> ExportAsync(
        ContentExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContentRoot);

        var types = ResolveTypes(request);
        var root = Path.GetFullPath(request.ContentRoot);
        var warnings = new List<string>();
        var changes = new List<ContentExportChange>();
        var writes = new List<(string Path, string Rendered)>();
        var deletions = new List<string>();
        var unchanged = 0;
        var examined = 0;
        var rejected = new List<string>();

        foreach (var type in types)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rows = await database.ContentItems
                .AsNoTracking()
                .Where(item => item.ContentType == type.Key)
                .Where(item => request.Key == null || item.ItemKey == request.Key)
                .OrderBy(item => item.ItemKey)
                .Select(item => new ExportRow(item.ItemKey, item.Body))
                .ToListAsync(cancellationToken);

            var directory = Path.Combine(root, type.Key);
            var produced = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                examined++;
                produced.Add(row.ItemKey + ".json");

                using var document = JsonDocument.Parse(row.Body);

                // Validated before it is rendered, so the message names the
                // document rather than the file that was about to be written.
                var validation = validator.Validate(
                    type, validator.CurrentVersion(type), document.RootElement);

                if (!validation.IsValid)
                {
                    rejected.Add(
                        $"{type.Key}/{row.ItemKey}.json: " + string.Join("; ", validation.Errors));

                    continue;
                }

                var rendered = canonical.Render(type.Key, document.RootElement);
                var path = Path.Combine(directory, row.ItemKey + ".json");
                var outcome = Compare(path, rendered);

                if (outcome is ContentExportOutcome.Unchanged)
                {
                    unchanged++;
                    continue;
                }

                changes.Add(new ContentExportChange(type.Key, row.ItemKey, outcome));
                writes.Add((path, rendered));
            }

            if (request.Key is null && request.Prune)
            {
                foreach (var stale in Stale(directory, type.Key, rows.Count, produced, warnings))
                {
                    changes.Add(new ContentExportChange(
                        type.Key,
                        Path.GetFileNameWithoutExtension(stale),
                        ContentExportOutcome.Removed));

                    deletions.Add(stale);
                }
            }
        }

        if (rejected.Count > 0)
        {
            // Nothing is written until every document has been rendered and
            // validated, so this leaves the tree exactly as it was found. A
            // half-written tree missing precisely the documents somebody needs
            // to look at is the worst of the available outcomes, and it is the
            // one a per-document write order produces.
            throw new InvalidOperationException(
                $"{rejected.Count} document(s) in the catalogue do not satisfy their schema, so " +
                "the exported tree would be rejected by the content repository's own CI. " +
                "Nothing was written:" + Environment.NewLine +
                string.Join(Environment.NewLine, rejected.Take(20)) +
                (rejected.Count > 20 ? $"{Environment.NewLine}... and {rejected.Count - 20} more." : ""));
        }

        if (!request.CheckOnly)
        {
            foreach (var (path, rendered) in writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, rendered, CanonicalContent.FileEncoding);
            }

            foreach (var path in deletions)
            {
                File.Delete(path);
            }
        }

        var result = new ContentExportResult(examined, unchanged, changes, warnings);

        foreach (var warning in warnings)
        {
            logger.LogWarning("Content export: {Warning}", warning);
        }

        logger.LogInformation(
            "Content export to {Root}: {Examined} examined, {Unchanged} unchanged, " +
            "{Added} added, {Changed} changed, {Removed} removed.{Mode}",
            root, examined, unchanged, result.Added, result.Changed, result.Removed,
            request.CheckOnly ? " Nothing was written." : string.Empty);

        return result;
    }

    /// <summary>Whether the file already holds exactly what was rendered.</summary>
    /// <remarks>
    /// Line endings are normalised before the comparison and only there. Which
    /// of the two a working tree holds is git's decision — the content
    /// repository pins them to LF, but a checkout made before it did will hold
    /// CRLF — and rewriting all 7,877 files to announce that would bury the one
    /// document somebody actually changed. Everything else about the file, down
    /// to the byte, has to match.
    /// </remarks>
    private static ContentExportOutcome Compare(string path, string rendered)
    {
        if (!File.Exists(path))
        {
            return ContentExportOutcome.Added;
        }

        var committed = File.ReadAllText(path, CanonicalContent.FileEncoding)
                            .Replace("\r\n", "\n", StringComparison.Ordinal);

        return string.Equals(committed, rendered, StringComparison.Ordinal)
            ? ContentExportOutcome.Unchanged
            : ContentExportOutcome.Changed;
    }

    /// <summary>
    /// Removes files of one type that the catalogue no longer publishes.
    /// </summary>
    /// <remarks>
    /// A withdrawn document has to stop being in the repository or the next
    /// import puts it back, so pruning is not optional for a full export. What
    /// is refused is pruning a type the database holds nothing for: that is
    /// what a half-applied migration, a filtered query or an empty database
    /// looks like, and it is far more likely than someone deliberately deleting
    /// every monster. The same reasoning the importer applies in the other
    /// direction, for the same reason.
    /// </remarks>
    private static IEnumerable<string> Stale(
        string directory,
        string typeKey,
        int rowCount,
        HashSet<string> produced,
        List<string> warnings)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(directory, "*.json")
                             .Order(StringComparer.Ordinal)
                             .ToList();

        if (rowCount == 0)
        {
            if (files.Count > 0)
            {
                warnings.Add(
                    $"The catalogue holds no '{typeKey}' documents, so the {files.Count} file(s) " +
                    "already in that directory were left alone. Emptying a whole type takes a " +
                    "deliberate act, not an export.");
            }

            return [];
        }

        return files.Where(file => !produced.Contains(Path.GetFileName(file)));
    }

    /// <summary>The content types this run covers.</summary>
    private static IReadOnlyList<ContentTypeDefinition> ResolveTypes(ContentExportRequest request)
    {
        if (request.Key is not null && request.ContentType is null)
        {
            throw new ArgumentException(
                "A key without a content type does not identify a document: keys are unique " +
                "within a type, not across the catalogue.", nameof(request));
        }

        if (request.Key is not null && !ContentSlug.IsValid(request.Key))
        {
            throw new ArgumentException(
                $"'{request.Key}' is not a valid content key.", nameof(request));
        }

        if (request.ContentType is null)
        {
            return ContentTypeRegistry.All;
        }

        if (!ContentTypeRegistry.TryResolve(request.ContentType, out var definition))
        {
            throw new ArgumentException(
                $"'{request.ContentType}' is not a content type. Known types: " +
                string.Join(", ", ContentTypeRegistry.All.Select(type => type.Key)),
                nameof(request));
        }

        return [definition];
    }

    private readonly record struct ExportRow(string ItemKey, string Body);
}
