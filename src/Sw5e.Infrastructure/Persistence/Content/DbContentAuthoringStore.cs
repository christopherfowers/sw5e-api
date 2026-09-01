using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Content;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// Drafting, publication, history and revert against the PostgreSQL catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the database store has one of these.</b> The file-backed store reads
/// a directory that is mounted read-only in every deployment, and builds its
/// index once at start-up and never again — so even a write that somehow
/// reached the disk would not be visible until the process restarted. Authoring
/// is registered alongside the database store and nowhere else, and the
/// endpoints answer 503 when it is absent rather than pretending to accept work
/// they cannot keep.
/// </para>
/// <para>
/// <b>Validation happens here, not at the endpoint.</b> Every path that can put
/// a document into the catalogue goes through this class, so this is the last
/// place a check is guaranteed to run. A schema check at the endpoint would be
/// bypassed by the next writer to arrive.
/// </para>
/// </remarks>
public sealed class DbContentAuthoringStore(
    Sw5eContentDbContext database,
    IContentSchemaValidator validator,
    TimeProvider clock) : IContentAuthoringStore
{
    /// <inheritdoc />
    public async Task<ContentDraft?> GetDraftAsync(
        ContentTypeDefinition type,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!ContentSlug.IsValid(key))
        {
            return null;
        }

        var row = await database.ContentDrafts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                draft => draft.ContentType == type.Key && draft.ItemKey == key,
                cancellationToken);

        return row is null ? null : ToDraft(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentDraftSummary>> ListDraftsAsync(
        CancellationToken cancellationToken = default)
    {
        var drafts = await database.ContentDrafts
            .AsNoTracking()
            .OrderByDescending(draft => draft.UpdatedAt)
            .ThenBy(draft => draft.ContentType)
            .ThenBy(draft => draft.ItemKey)
            .ToListAsync(cancellationToken);

        if (drafts.Count == 0)
        {
            return [];
        }

        // Which of these drafts name a document that already exists, and
        // whether the version they were started from is still the current one.
        // Answered in one pass rather than per draft: the worklist is a single
        // screen, and a query per row is how a page that renders instantly with
        // three drafts stops rendering at all with three hundred.
        var keys = drafts.Select(draft => draft.ContentType).Distinct().ToArray();

        var existing = await database.ContentItems
            .Where(item => keys.Contains(item.ContentType))
            .Select(item => new { item.ContentType, item.ItemKey })
            .ToListAsync(cancellationToken);

        var present = existing
            .Select(item => (item.ContentType, item.ItemKey))
            .ToHashSet();

        var latest = await LatestRevisionIdsAsync(drafts.Select(
            draft => (draft.ContentType, draft.ItemKey)), cancellationToken);

        return [.. drafts.Select(draft => new ContentDraftSummary(
            draft.ContentType,
            draft.ItemKey,
            draft.Name,
            present.Contains((draft.ContentType, draft.ItemKey)),
            IsCurrent(draft, latest),
            draft.CreatedByUserId,
            draft.UpdatedByUserId,
            draft.ResolvesFlagId,
            draft.CreatedAt,
            draft.UpdatedAt))];
    }

    /// <inheritdoc />
    public async Task<ContentAuthoringResult> SaveDraftAsync(
        ContentTypeDefinition type,
        string key,
        JsonElement body,
        Guid actorUserId,
        Guid? resolvesFlagId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        var prepared = Prepare(type, key, body);

        if (prepared.Result is not null)
        {
            return prepared.Result;
        }

        var item = prepared.Item!;
        var now = clock.GetUtcNow();

        var existing = await database.ContentDrafts.SingleOrDefaultAsync(
            draft => draft.ContentType == type.Key && draft.ItemKey == key,
            cancellationToken);

        var baseRevisionId = await CurrentRevisionIdAsync(type.Key, key, cancellationToken);

        if (existing is null)
        {
            database.ContentDrafts.Add(new ContentDraftRow
            {
                ContentType = type.Key,
                ItemKey = key,
                Name = item.Name,
                Body = item.Body.GetRawText(),
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                BaseRevisionId = baseRevisionId,
                ResolvesFlagId = resolvesFlagId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Name = item.Name;
            existing.Body = item.Body.GetRawText();
            existing.UpdatedByUserId = actorUserId;
            existing.UpdatedAt = now;

            // A caller that does not name a flag is editing the draft, not
            // detaching it from the report it was raised against. Clearing the
            // link on every save would quietly break the loop the review queue
            // depends on.
            if (resolvesFlagId is not null)
            {
                existing.ResolvesFlagId = resolvesFlagId;
            }
        }

        await database.SaveChangesAsync(cancellationToken);

        return ContentAuthoringResult.Succeeded();
    }

    /// <inheritdoc />
    public async Task<bool> DiscardDraftAsync(
        ContentTypeDefinition type,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!ContentSlug.IsValid(key))
        {
            return false;
        }

        var removed = await database.ContentDrafts
            .Where(draft => draft.ContentType == type.Key && draft.ItemKey == key)
            .ExecuteDeleteAsync(cancellationToken);

        return removed > 0;
    }

    /// <inheritdoc />
    public Task<ContentAuthoringResult> PublishDraftAsync(
        ContentTypeDefinition type,
        string key,
        Guid actorUserId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        return InTransactionAsync(async token =>
        {
            var draft = await database.ContentDrafts.SingleOrDefaultAsync(
                candidate => candidate.ContentType == type.Key && candidate.ItemKey == key,
                token);

            if (draft is null)
            {
                return ContentAuthoringResult.NotFound;
            }

            JsonElement body;

            using (var document = JsonDocument.Parse(draft.Body))
            {
                body = document.RootElement.Clone();
            }

            // Revalidated at the moment of publication, not trusted from when
            // the draft was saved. The schema can have moved in between, and
            // the check that decides what enters the corpus has to be the one
            // that runs as it enters.
            var prepared = Prepare(type, key, body);

            if (prepared.Result is not null)
            {
                return prepared.Result;
            }

            var current = await CurrentRevisionIdAsync(type.Key, key, token);

            // Somebody else published this document while the draft was open.
            // Refused rather than merged: this store has no way to know which
            // of two prose rewrites was meant to win, and picking one silently
            // discards work somebody did.
            if (draft.BaseRevisionId != current)
            {
                return ContentAuthoringResult.Stale;
            }

            var revision = await ApplyAsync(
                type,
                key,
                prepared.Item!,
                actorUserId,
                reason,
                revertedFrom: null,
                prepared.SchemaVersion,
                token);

            database.ContentDrafts.Remove(draft);

            await database.SaveChangesAsync(token);

            return ContentAuthoringResult.Succeeded(revision);
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentRevisionSummary>> ListRevisionsAsync(
        ContentTypeDefinition type,
        string key,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!ContentSlug.IsValid(key))
        {
            return [];
        }

        var rows = await database.ContentRevisions
            .AsNoTracking()
            .Where(revision => revision.ContentType == type.Key && revision.ItemKey == key)
            .OrderByDescending(revision => revision.Number)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToSummary)];
    }

    /// <inheritdoc />
    public async Task<ContentRevision?> GetRevisionAsync(
        ContentTypeDefinition type,
        string key,
        long revisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!ContentSlug.IsValid(key))
        {
            return null;
        }

        // Scoped to the type and key from the route rather than fetched by id
        // alone, so a caller cannot read one document's history through
        // another's URL.
        var row = await database.ContentRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                revision => revision.Id == revisionId &&
                            revision.ContentType == type.Key &&
                            revision.ItemKey == key,
                cancellationToken);

        if (row is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(row.Body);

        return new ContentRevision(
            row.Id,
            row.ContentType,
            row.ItemKey,
            row.Number,
            document.RootElement.Clone(),
            ContentAuthoringWire.ToAction(row.Action),
            row.ActorUserId,
            row.Reason,
            row.SchemaVersion,
            row.RevertedFromId,
            row.CreatedAt);
    }

    /// <inheritdoc />
    public Task<ContentAuthoringResult> RevertAsync(
        ContentTypeDefinition type,
        string key,
        long revisionId,
        Guid actorUserId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);

        return InTransactionAsync(async token =>
        {
            if (!ContentSlug.IsValid(key))
            {
                return ContentAuthoringResult.NotFound;
            }

            var target = await database.ContentRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    revision => revision.Id == revisionId &&
                                revision.ContentType == type.Key &&
                                revision.ItemKey == key,
                    token);

            if (target is null)
            {
                return ContentAuthoringResult.NotFound;
            }

            JsonElement body;

            using (var document = JsonDocument.Parse(target.Body))
            {
                body = document.RootElement.Clone();
            }

            var prepared = Prepare(type, key, body);

            if (prepared.Result is not null)
            {
                return prepared.Result;
            }

            var revision = await ApplyAsync(
                type,
                key,
                prepared.Item!,
                actorUserId,
                reason,
                revertedFrom: target.Id,
                prepared.SchemaVersion,
                token);

            await database.SaveChangesAsync(token);

            return ContentAuthoringResult.Succeeded(revision);
        });
    }

    /// <summary>
    /// Projects and validates a document, producing either the projection or
    /// the refusal.
    /// </summary>
    private PreparedDocument Prepare(ContentTypeDefinition type, string key, JsonElement body)
    {
        if (!ContentSlug.IsValid(key))
        {
            return new PreparedDocument(
                null,
                0,
                ContentAuthoringResult.Invalid(["The item key is not a valid slug."]));
        }

        var version = ContentIndexBuilder.ComputeVersionFor(body);
        var item = ContentIndexBuilder.TryProject(type, key, body, version, out var failure);

        if (item is null)
        {
            return new PreparedDocument(null, 0, ContentAuthoringResult.Invalid([failure!]));
        }

        var schemaVersion = validator.CurrentVersion(type);
        var validation = validator.Validate(type, schemaVersion, body);

        return validation.IsValid
            ? new PreparedDocument(item, schemaVersion, null)
            : new PreparedDocument(null, schemaVersion, ContentAuthoringResult.Invalid(validation.Errors));
    }

    /// <summary>
    /// Writes a document into the catalogue and records the revision for it.
    /// </summary>
    private async Task<ContentRevisionSummary> ApplyAsync(
        ContentTypeDefinition type,
        string key,
        IndexedContentItem item,
        Guid actorUserId,
        string? reason,
        long? revertedFrom,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var row = await database.ContentItems.SingleOrDefaultAsync(
            candidate => candidate.ContentType == type.Key && candidate.ItemKey == key,
            cancellationToken);

        var created = row is null;

        if (row is null)
        {
            row = ContentImporter.Project(item, now);
            database.ContentItems.Add(row);
        }
        else
        {
            // The imported corpus carries no history: it was loaded by the
            // deploy-time importer, which records nothing about who wrote it
            // because nobody did. The first person to edit such a document
            // would otherwise have nothing to revert to, so the state as it
            // stood before this change is written down first, attributed to no
            // one. Done lazily rather than by having the importer write 7,877
            // baseline rows on every deploy.
            await EnsureBaselineAsync(row, schemaVersion, cancellationToken);

            ContentImporter.Apply(row, item, now);
        }

        await database.SaveChangesAsync(cancellationToken);

        var number = await NextRevisionNumberAsync(type.Key, key, cancellationToken);

        var revision = new ContentRevisionRow
        {
            ContentType = type.Key,
            ItemKey = key,
            Number = number,
            Name = item.Name,
            Body = item.Body.GetRawText(),
            Version = item.Version,
            Action = ContentAuthoringWire.From(
                revertedFrom is not null
                    ? ContentRevisionAction.Reverted
                    : created
                        ? ContentRevisionAction.Created
                        : ContentRevisionAction.Updated),
            ActorUserId = actorUserId,
            Reason = reason,
            SchemaVersion = schemaVersion,
            RevertedFromId = revertedFrom,
            CreatedAt = now,
        };

        database.ContentRevisions.Add(revision);

        await database.SaveChangesAsync(cancellationToken);

        await RewriteReferencesAsync(row, item, cancellationToken);

        return ToSummary(revision);
    }

    /// <summary>
    /// Records what a never-edited document said before anybody touched it.
    /// </summary>
    private async Task EnsureBaselineAsync(
        ContentItemRow row,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        var any = await database.ContentRevisions.AnyAsync(
            revision => revision.ContentType == row.ContentType && revision.ItemKey == row.ItemKey,
            cancellationToken);

        if (any)
        {
            return;
        }

        database.ContentRevisions.Add(new ContentRevisionRow
        {
            ContentType = row.ContentType,
            ItemKey = row.ItemKey,
            Number = 1,
            Name = row.Name,
            Body = row.Body,
            Version = row.Version,
            Action = ContentAuthoringWire.From(ContentRevisionAction.Imported),

            // No actor. The importer is not a person, and attributing its work
            // to whoever happened to edit the document first would be a lie
            // written into the audit trail.
            ActorUserId = null,
            Reason = null,
            SchemaVersion = schemaVersion,
            RevertedFromId = null,
            CreatedAt = row.UpdatedAt,
        });

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces one item's outgoing edges and re-resolves the edges its
    /// publication could have changed.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. The importer re-resolves the entire graph because it
    /// has just rewritten an unknown part of the corpus; a single publication
    /// can only affect this item's own edges and the edges that were looking
    /// for this item. Both are index lookups, so publishing stays cheap however
    /// large the corpus grows.
    /// </remarks>
    private async Task RewriteReferencesAsync(
        ContentItemRow row,
        IndexedContentItem item,
        CancellationToken cancellationToken)
    {
        await database.ContentReferences
            .Where(reference => reference.FromItemId == row.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var catalogue = await database.ContentItems
            .Select(candidate => new { candidate.Id, candidate.ContentType, candidate.ItemKey, candidate.Name })
            .ToListAsync(cancellationToken);

        var byKey = catalogue.ToDictionary(
            entry => (entry.ContentType, entry.ItemKey),
            entry => entry.Id);

        var byName = catalogue
            .GroupBy(entry => (entry.ContentType, entry.Name))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Id);

        foreach (var extracted in ContentReferenceMap.Extract(item.Type.Key, item.Body))
        {
            long? resolved = extracted.TargetKind == ContentReferenceTargetKind.Key
                ? byKey.TryGetValue((extracted.TargetType, extracted.TargetIdentifier), out var byKeyId)
                    ? byKeyId
                    : null
                : byName.TryGetValue((extracted.TargetType, extracted.TargetIdentifier), out var byNameId)
                    ? byNameId
                    : null;

            database.ContentReferences.Add(new ContentReferenceRow
            {
                FromItemId = row.Id,
                Relation = extracted.Relation,
                JsonPath = extracted.JsonPath,
                TargetType = extracted.TargetType,
                TargetKind = extracted.TargetKind,
                TargetIdentifier = extracted.TargetIdentifier,
                Ordinal = extracted.Ordinal,
                ResolvedItemId = resolved,
            });
        }

        // Edges elsewhere in the corpus that were waiting for this item, and
        // edges that already pointed at it and may no longer match now its name
        // has changed.
        var affected = await database.ContentReferences
            .Where(reference =>
                reference.ResolvedItemId == row.Id ||
                (reference.ResolvedItemId == null &&
                 reference.TargetType == row.ContentType &&
                 (reference.TargetIdentifier == row.ItemKey || reference.TargetIdentifier == row.Name)))
            .ToListAsync(cancellationToken);

        foreach (var reference in affected)
        {
            reference.ResolvedItemId = reference.TargetKind == ContentReferenceTargetKind.Key
                ? byKey.TryGetValue((reference.TargetType, reference.TargetIdentifier), out var keyId)
                    ? keyId
                    : null
                : byName.TryGetValue((reference.TargetType, reference.TargetIdentifier), out var nameId)
                    ? nameId
                    : null;
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<long?> CurrentRevisionIdAsync(
        string type,
        string key,
        CancellationToken cancellationToken)
    {
        var latest = await database.ContentRevisions
            .Where(revision => revision.ContentType == type && revision.ItemKey == key)
            .OrderByDescending(revision => revision.Number)
            .Select(revision => (long?)revision.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return latest;
    }

    private async Task<int> NextRevisionNumberAsync(
        string type,
        string key,
        CancellationToken cancellationToken)
    {
        var highest = await database.ContentRevisions
            .Where(revision => revision.ContentType == type && revision.ItemKey == key)
            .MaxAsync(revision => (int?)revision.Number, cancellationToken);

        return (highest ?? 0) + 1;
    }

    private async Task<Dictionary<(string Type, string Key), long>> LatestRevisionIdsAsync(
        IEnumerable<(string Type, string Key)> items,
        CancellationToken cancellationToken)
    {
        var types = items.Select(item => item.Type).Distinct().ToArray();

        var rows = await database.ContentRevisions
            .Where(revision => types.Contains(revision.ContentType))
            .GroupBy(revision => new { revision.ContentType, revision.ItemKey })
            .Select(group => new
            {
                group.Key.ContentType,
                group.Key.ItemKey,
                Id = group.OrderByDescending(revision => revision.Number)
                          .Select(revision => revision.Id)
                          .First(),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => (row.ContentType, row.ItemKey), row => row.Id);
    }

    private static bool IsCurrent(
        ContentDraftRow draft,
        Dictionary<(string Type, string Key), long> latest) =>
        latest.TryGetValue((draft.ContentType, draft.ItemKey), out var id)
            ? draft.BaseRevisionId == id
            : draft.BaseRevisionId is null;

    /// <summary>
    /// Runs a unit of work inside one transaction, through the execution
    /// strategy the retrying connection requires.
    /// </summary>
    private Task<ContentAuthoringResult> InTransactionAsync(
        Func<CancellationToken, Task<ContentAuthoringResult>> work) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();

            await using var transaction = await database.Database.BeginTransactionAsync();

            var result = await work(CancellationToken.None);

            // A refusal must leave nothing behind. Publishing writes the
            // catalogue row before it writes the revision, so a validation
            // failure discovered late without this would leave the document
            // changed and unrecorded — the one outcome this whole path exists
            // to make impossible.
            if (result.Status != ContentAuthoringStatus.Succeeded)
            {
                await transaction.RollbackAsync();
                return result;
            }

            await transaction.CommitAsync();

            return result;
        });

    private static ContentDraft ToDraft(ContentDraftRow row)
    {
        using var document = JsonDocument.Parse(row.Body);

        return new ContentDraft(
            row.ContentType,
            row.ItemKey,
            document.RootElement.Clone(),
            row.CreatedByUserId,
            row.UpdatedByUserId,
            row.BaseRevisionId,
            row.ResolvesFlagId,
            row.CreatedAt,
            row.UpdatedAt);
    }

    private static ContentRevisionSummary ToSummary(ContentRevisionRow row) =>
        new(row.Id,
            row.ContentType,
            row.ItemKey,
            row.Number,
            ContentAuthoringWire.ToAction(row.Action),
            row.ActorUserId,
            row.Reason,
            row.RevertedFromId,
            row.CreatedAt);

    private sealed record PreparedDocument(
        IndexedContentItem? Item,
        int SchemaVersion,
        ContentAuthoringResult? Result);
}
