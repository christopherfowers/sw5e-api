using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Content;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// What one import run did.
/// </summary>
/// <remarks>
/// Broken down rather than reported as a single total because the numbers are
/// how a deploy is verified. "136 items" tells an operator nothing; "0 inserted,
/// 0 updated, 136 unchanged" says the corpus in the database already matches the
/// one that shipped, and "136 inserted" after a deploy that changed no content
/// says something wiped the catalogue.
/// </remarks>
/// <param name="Inserted">Items that were not previously in the database.</param>
/// <param name="Updated">Items whose document had changed.</param>
/// <param name="Unchanged">Items already present with an identical document.</param>
/// <param name="Deleted">Items removed from the database because the corpus no longer has them.</param>
/// <param name="ReferencesWritten">Cross-reference edges written for inserted and updated items.</param>
/// <param name="ReferencesUnresolved">
/// Edges whose target is not in the catalogue, or whose name matched more than
/// one item. Expected to be non-zero against an in-progress corpus; a sudden
/// jump is what says something has gone wrong.
/// </param>
/// <param name="Warnings">
/// Diagnostics naming files, unresolved targets and refused operations. Log
/// these; they name filesystem paths and are not fit to return from an endpoint.
/// </param>
public sealed record ContentImportResult(
    int Inserted,
    int Updated,
    int Unchanged,
    int Deleted,
    int ReferencesWritten,
    int ReferencesUnresolved,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Loads the canonical JSON content into PostgreSQL, and can be run again over
/// the same corpus without changing anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why idempotence is a requirement and not a nicety.</b> This runs from a
/// deploy-time migrator, and a deploy can be retried: a network blip, a rolled
/// back release, an operator who is not sure whether the last one finished. An
/// importer that appended, or that deleted and re-inserted, would make "run it
/// again to be sure" a destructive act — it would churn every row, bump every
/// version, and invalidate every cached response in front of the API for a
/// corpus that did not change. So the unit of work is a comparison: each
/// document's content hash is checked against the stored one, and a row is
/// written only when it actually differs.
/// </para>
/// <para>
/// <b>Why it reuses the file store's scanner.</b> Which files count as content,
/// how a display name is found on a type that calls it "title", how the row
/// projection and search text are derived — every one of those is a decision
/// both stores have to make the same way, or switching stores changes what the
/// site shows. Rather than restating them, this reads the corpus through
/// <see cref="ContentIndexBuilder"/>, the same scan the file-backed repository
/// is built from. The projected columns in the database are then that scan's
/// output, written down.
/// </para>
/// <para>
/// <b>What it refuses to do.</b> An import that finds nothing does not empty
/// the catalogue, and an import that finds no items of a given type does not
/// empty that type. Both of those are what a wrong path or a half-mounted
/// volume looks like, and both are far more likely than someone genuinely
/// deleting every monster. Deletion is scoped to types the scan actually found
/// content for, so removing an item still propagates and removing a whole type
/// needs a deliberate act.
/// </para>
/// </remarks>
public sealed class ContentImporter(
    Sw5eContentDbContext database,
    ILogger<ContentImporter> logger)
{
    /// <summary>
    /// Imports every content document under <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="rootPath">
    /// Directory holding one subdirectory per content type. Need not exist; a
    /// missing directory produces warnings and no changes.
    /// </param>
    public async Task<ContentImportResult> ImportAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        // The connection is configured to retry transient failures, and EF
        // refuses to combine that with a transaction opened by hand unless the
        // whole unit of work is handed to the execution strategy — because a
        // retry has to restart the transaction, not resume it. Wrapping the run
        // is safe precisely because the run is idempotent: a second attempt
        // over the same corpus reaches the same end state as the first.
        var strategy = database.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retried attempt starts from a context that still tracks
            // everything the failed one loaded and added. Reusing that state
            // would re-insert rows the rolled-back transaction disposed of.
            database.ChangeTracker.Clear();

            return await RunImportAsync(rootPath, cancellationToken);
        });
    }

    private async Task<ContentImportResult> RunImportAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var scan = ContentIndexBuilder.Build(rootPath);
        var warnings = new List<string>(scan.Warnings);

        foreach (var warning in scan.Warnings)
        {
            logger.LogWarning("Content import: {Warning}", warning);
        }

        // One transaction for the whole run. A half-applied import leaves the
        // API serving a catalogue that is internally inconsistent — items whose
        // references point at rows that were never written — and there is no
        // safe way to resume from it, so the only sane outcome of a failure is
        // that nothing happened.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var existing = await database.ContentItems
            .Select(item => new ExistingItem(item.Id, item.ContentType, item.ItemKey, item.Version))
            .ToDictionaryAsync(item => (item.ContentType, item.ItemKey), cancellationToken);

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        // Items whose edges have to be rewritten: everything inserted or
        // updated. An unchanged item's document did not change, so neither did
        // the edges read out of it.
        var touchedIds = new HashSet<long>();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in scan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!existing.TryGetValue((item.Type.Key, item.Key), out var current))
            {
                database.ContentItems.Add(Project(item, now));
                inserted++;
                continue;
            }

            if (string.Equals(current.Version, item.Version, StringComparison.Ordinal))
            {
                // Identical document. Not even a touched timestamp: an import
                // that changes nothing must be indistinguishable from one that
                // never ran.
                unchanged++;
                continue;
            }

            var row = await database.ContentItems.SingleAsync(
                candidate => candidate.Id == current.Id, cancellationToken);

            Apply(row, item, now);
            touchedIds.Add(current.Id);
            updated++;
        }

        await database.SaveChangesAsync(cancellationToken);

        // Rows that were inserted only received their identity in that save, so
        // their ids are collected afterwards. Everything tracked here came out
        // of the scan and is either an insert or an update; unchanged items
        // were never loaded.
        foreach (var entry in database.ChangeTracker.Entries<ContentItemRow>())
        {
            touchedIds.Add(entry.Entity.Id);
        }

        var deleted = await PruneAsync(scan.Items, warnings, cancellationToken);
        var referencesWritten = await RewriteReferencesAsync(scan.Items, touchedIds, cancellationToken);
        var unresolved = await ResolveReferencesAsync(warnings, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var result = new ContentImportResult(
            inserted, updated, unchanged, deleted, referencesWritten, unresolved, warnings);

        logger.LogInformation(
            "Content import from {RootPath}: {Inserted} inserted, {Updated} updated, " +
            "{Unchanged} unchanged, {Deleted} deleted, {References} references written, " +
            "{Unresolved} unresolved.",
            rootPath, inserted, updated, unchanged, deleted, referencesWritten, unresolved);

        return result;
    }

    /// <summary>
    /// Removes rows the corpus no longer holds, without letting a failed scan
    /// look like a deletion.
    /// </summary>
    private async Task<int> PruneAsync(
        IReadOnlyList<IndexedContentItem> items,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // Types the scan actually produced content for. A type absent from this
        // set was not "emptied by the content repository", it was not read —
        // an unmounted volume, a wrong path, a directory that failed to copy —
        // and treating the two the same is how a deploy accident becomes data
        // loss.
        var scannedTypes = items
            .Select(item => item.Type.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (scannedTypes.Count == 0)
        {
            warnings.Add(
                "The content scan produced no items, so nothing was deleted. " +
                "An empty scan is treated as a failed read rather than as an empty corpus.");

            return 0;
        }

        foreach (var definition in ContentTypeRegistry.All)
        {
            if (!scannedTypes.Contains(definition.Key))
            {
                warnings.Add(
                    $"No content was found for type '{definition.Key}', so rows of that type " +
                    "were left alone. Deleting every item of a type requires a deliberate act.");
            }
        }

        var keptByType = items
            .GroupBy(item => item.Type.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Key).ToArray(),
                StringComparer.Ordinal);

        var deleted = 0;

        foreach (var (type, keys) in keptByType)
        {
            // Edges owned by a deleted item go with it (cascade); edges that
            // pointed *at* it are set to unresolved rather than deleted, which
            // is what the foreign key on resolved_item_id is configured for.
            deleted += await database.ContentItems
                .Where(item => item.ContentType == type && !keys.Contains(item.ItemKey))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return deleted;
    }

    /// <summary>
    /// Replaces the outgoing edges of every item that was inserted or updated.
    /// </summary>
    /// <remarks>
    /// Delete-then-insert per item rather than a diff: an item has a handful of
    /// edges, they are wholly determined by its document, and reconciling them
    /// individually would be more code guarding a case — an edge whose path
    /// survived but whose target changed — that a rewrite handles for free.
    /// Untouched items keep their edges, which is why an unchanged import does
    /// no writes here either.
    /// </remarks>
    private async Task<int> RewriteReferencesAsync(
        IReadOnlyList<IndexedContentItem> items,
        HashSet<long> touched,
        CancellationToken cancellationToken)
    {
        if (touched.Count == 0)
        {
            return 0;
        }

        var touchedIds = touched.ToArray();

        await database.ContentReferences
            .Where(reference => touchedIds.Contains(reference.FromItemId))
            .ExecuteDeleteAsync(cancellationToken);

        var idByIdentity = await database.ContentItems
            .Where(item => touchedIds.Contains(item.Id))
            .Select(item => new { item.Id, item.ContentType, item.ItemKey })
            .ToDictionaryAsync(item => (item.ContentType, item.ItemKey), item => item.Id, cancellationToken);

        var written = 0;

        foreach (var item in items)
        {
            if (!idByIdentity.TryGetValue((item.Type.Key, item.Key), out var itemId))
            {
                continue;
            }

            foreach (var extracted in ContentReferenceMap.Extract(item.Type.Key, item.Body))
            {
                database.ContentReferences.Add(new ContentReferenceRow
                {
                    FromItemId = itemId,
                    Relation = extracted.Relation,
                    JsonPath = extracted.JsonPath,
                    TargetType = extracted.TargetType,
                    TargetKind = extracted.TargetKind,
                    TargetIdentifier = extracted.TargetIdentifier,
                    Ordinal = extracted.Ordinal,
                });

                written++;
            }
        }

        await database.SaveChangesAsync(cancellationToken);

        return written;
    }

    /// <summary>
    /// Recomputes which edges reach a real item, and returns how many do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every edge is re-resolved, not only the ones just written, because
    /// resolution depends on the whole catalogue rather than on the document
    /// the edge came from: importing the missing power is what turns six
    /// dangling prerequisites into six working links, and none of those six
    /// documents changed.
    /// </para>
    /// <para>
    /// Matching happens here rather than in SQL so it uses the same comparer as
    /// everything else in this codebase. A join on <c>lower(name)</c> would
    /// hand the decision to the database's collation, which is configurable,
    /// installation-specific, and not the rule the rest of the content pipeline
    /// applies.
    /// </para>
    /// </remarks>
    private async Task<int> ResolveReferencesAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var catalogue = await database.ContentItems
            .Select(item => new CatalogueEntry(item.Id, item.ContentType, item.ItemKey, item.Name))
            .ToListAsync(cancellationToken);

        var byKey = catalogue.ToDictionary(
            entry => (entry.ContentType, entry.ItemKey),
            entry => entry.Id);

        // Grouped rather than a dictionary: a name that matches two items must
        // resolve to neither, and only a grouping makes that distinguishable
        // from a name that matches one.
        var byName = catalogue
            .GroupBy(
                entry => (entry.ContentType, entry.Name),
                ContentIdentityComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Id).ToArray(),
                ContentIdentityComparer.Instance);

        var references = await database.ContentReferences.ToListAsync(cancellationToken);
        var unresolved = 0;

        foreach (var reference in references)
        {
            long? target = reference.TargetKind switch
            {
                ContentReferenceTargetKind.Key =>
                    byKey.TryGetValue((reference.TargetType, reference.TargetIdentifier), out var id)
                        ? id
                        : null,

                _ => byName.TryGetValue((reference.TargetType, reference.TargetIdentifier), out var ids) &&
                     ids.Length == 1
                        ? ids[0]
                        : null,
            };

            reference.ResolvedItemId = target;

            if (target is not null)
            {
                continue;
            }

            unresolved++;

            warnings.Add(
                $"Unresolved reference: {reference.Relation} at {reference.JsonPath} " +
                $"points at {reference.TargetType} '{reference.TargetIdentifier}', which is " +
                "not in the catalogue or is not uniquely named.");
        }

        await database.SaveChangesAsync(cancellationToken);

        return unresolved;
    }

    /// <summary>
    /// Builds a catalogue row from a scanned or authored document.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because the authoring store writes the same
    /// row from a document that arrived through an endpoint. The projected
    /// columns are what every list, filter, sort and search reads, and the two
    /// stores are held to parity on all of them, so a published document has to
    /// be projected by this code and not by a second copy of it.
    /// </remarks>
    internal static ContentItemRow Project(IndexedContentItem item, DateTimeOffset now)
    {
        var row = new ContentItemRow
        {
            ContentType = item.Type.Key,
            ItemKey = item.Key,
            Name = item.Name,
            Facets = "{}",
            Body = "{}",
            SearchText = string.Empty,
            Version = item.Version,
            NameLower = item.NameLower,
            SearchTextLower = item.SearchTextLower,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(row, item, now);

        return row;
    }

    /// <summary>Copies a document's projection onto an existing row.</summary>
    internal static void Apply(ContentItemRow row, IndexedContentItem item, DateTimeOffset now)
    {
        row.Name = item.Name;
        row.SourceKey = item.SourceKey;
        row.ContentSet = item.ContentSet;
        row.Summary = item.Summary;
        row.Facets = JsonSerializer.Serialize(item.Facets);
        row.Body = item.Body.GetRawText();
        row.SearchText = item.SearchText;
        row.NameLower = item.NameLower;
        row.SearchTextLower = item.SearchTextLower;
        row.Version = item.Version;
        row.UpdatedAt = now;
    }

    private sealed record ExistingItem(long Id, string ContentType, string ItemKey, string Version);

    private sealed record CatalogueEntry(long Id, string ContentType, string ItemKey, string Name);

    /// <summary>
    /// Compares a (type, identifier) pair with the type matched exactly and the
    /// identifier matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// The type half is a compile-time constant on both sides, so folding it
    /// would only hide a bug. The identifier half comes from prose transcribed
    /// out of a book, where "Force-Sensitive" and "Force-sensitive" are the same
    /// feat and a case difference is a typo rather than a distinction.
    /// </remarks>
    private sealed class ContentIdentityComparer : IEqualityComparer<(string Type, string Identifier)>
    {
        public static ContentIdentityComparer Instance { get; } = new();

        public bool Equals((string Type, string Identifier) left, (string Type, string Identifier) right) =>
            string.Equals(left.Type, right.Type, StringComparison.Ordinal) &&
            string.Equals(left.Identifier, right.Identifier, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Type, string Identifier) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.Type),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Identifier));
    }
}
