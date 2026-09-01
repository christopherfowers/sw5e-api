using Microsoft.EntityFrameworkCore;
using Sw5e.Domain.Content;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>
/// The content half of the SW5e database: the catalogue and the graph of links
/// between its items.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why its own context and its own schema.</b> Content and identity share a
/// database but nothing else. They are written by different pipelines — content
/// by a deploy-time importer from a reviewed repository, identity by users at
/// runtime — restored on different schedules, and read by code that has no
/// business seeing the other's tables. Splitting them into two contexts over
/// two PostgreSQL schemas means each owns its own migration history, so a
/// content migration and an identity migration can be authored, reviewed and
/// applied independently without one rebasing on the other.
/// </para>
/// <para>
/// They do not share a connection. <c>AddSw5ePersistence</c> registers this
/// context's data source under a service key rather than as a plain singleton,
/// so identity cannot pick it up by accident: identity resolves its own
/// connection string, which a deployment may point at a least-privileged role
/// or a separate database entirely.
/// </para>
/// <para>
/// What both contexts must do — and what the identity context also does — is
/// keep the migration history table inside their own schema. Pointing both at
/// the default <c>public.__EFMigrationsHistory</c> makes each read the other's
/// rows as unknown migrations of its own, and the next <c>Migrate</c> on either
/// tries to create tables that already exist. Nothing about the content schema
/// looks wrong when that happens.
/// </para>
/// </remarks>
public sealed class Sw5eContentDbContext(DbContextOptions<Sw5eContentDbContext> options)
    : DbContext(options)
{
    /// <summary>PostgreSQL schema every table in this context lives in.</summary>
    public const string SchemaName = "content";

    /// <summary>
    /// Migration history table for this context, kept inside
    /// <see cref="SchemaName"/> so it cannot collide with another context's.
    /// </summary>
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>The catalogue: one row per content document.</summary>
    public DbSet<ContentItemRow> ContentItems => Set<ContentItemRow>();

    /// <summary>The graph: one row per cross-reference found in a document.</summary>
    public DbSet<ContentReferenceRow> ContentReferences => Set<ContentReferenceRow>();

    /// <summary>The type registry, mirrored so the type column can be constrained.</summary>
    public DbSet<ContentTypeRow> ContentTypes => Set<ContentTypeRow>();

    /// <summary>The change history: append-only, one row per recorded change.</summary>
    public DbSet<ContentRevisionRow> ContentRevisions => Set<ContentRevisionRow>();

    /// <summary>Work in progress that is not live.</summary>
    public DbSet<ContentDraftRow> ContentDrafts => Set<ContentDraftRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        // Trigram indexes are what make the substring filters this API is built
        // around index-usable. `name LIKE '%wook%'` cannot use a btree index at
        // all — the leading wildcard defeats it — so without pg_trgm every name
        // filter and every free-text search is a sequential scan of the whole
        // type. That is survivable at 136 items and is not at 7,000.
        modelBuilder.HasPostgresExtension("pg_trgm");

        ConfigureContentTypes(modelBuilder);
        ConfigureContentItems(modelBuilder);
        ConfigureContentReferences(modelBuilder);
        ConfigureContentRevisions(modelBuilder);
        ConfigureContentDrafts(modelBuilder);

        ApplyByteOrderCollation(modelBuilder);
    }

    /// <summary>
    /// Pins every text column in this schema to the <c>C</c> collation, so
    /// PostgreSQL compares and orders them byte by byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the most consequential decision in the file. PostgreSQL's
    /// default collation is whatever the database was initialised with —
    /// <c>en_US.utf8</c> on one host, <c>C.UTF-8</c> in a container image,
    /// something else on a developer's machine — and under a locale collation
    /// punctuation is weighted differently or ignored outright. "Twi'lek" then
    /// sorts somewhere other than where <see cref="StringComparer.Ordinal"/>
    /// puts it, so the same page of species comes back in a different order
    /// depending on which store answered and on which machine the database was
    /// created. Byte order is the only collation that agrees with .NET's
    /// ordinal comparison, and that agreement is what makes swapping the two
    /// stores invisible to the site.
    /// </para>
    /// <para>
    /// It is applied per column rather than through the model-level
    /// <c>UseCollation</c>, which sets the collation of the <em>database</em>
    /// and therefore only takes effect if this application creates it. It does
    /// not: the database is provisioned by the compose stack and the migrator
    /// only ever runs against an existing one, so a model-level setting would
    /// be silently ignored and the columns would inherit the host's locale
    /// after all — which is the failure this exists to prevent, arriving
    /// looking like it had been prevented.
    /// </para>
    /// <para>
    /// What decides is the type the column is <em>stored</em> as, not the type
    /// the property has. <c>target_kind</c> is an enum in C# and text in
    /// PostgreSQL because of a value converter, and a check on the CLR type
    /// alone skips it — leaving one column ordered by the host's locale in a
    /// schema where everything else is not.
    /// </para>
    /// <para>
    /// jsonb columns are skipped because jsonb is not a collatable type;
    /// asking for a collation on one is an error at migration time.
    /// </para>
    /// </remarks>
    private static void ApplyByteOrderCollation(ModelBuilder modelBuilder)
    {
        foreach (var property in modelBuilder.Model
                     .GetEntityTypes()
                     .SelectMany(entityType => entityType.GetProperties()))
        {
            var storedAs =
                property.GetValueConverter()?.ProviderClrType
                ?? property.GetProviderClrType()
                ?? property.ClrType;

            if (storedAs == typeof(string) &&
                !string.Equals(property.GetColumnType(), "jsonb", StringComparison.Ordinal))
            {
                property.SetCollation("C");
            }
        }
    }

    private static void ConfigureContentTypes(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ContentTypeRow>(entity =>
        {
            entity.ToTable("content_type");
            entity.HasKey(type => type.Key);

            entity.Property(type => type.Key).HasColumnName("key").HasMaxLength(64);
            entity.Property(type => type.DisplayName).HasColumnName("display_name").HasMaxLength(64);
            entity.Property(type => type.PluralName).HasColumnName("plural_name").HasMaxLength(64);
            entity.Property(type => type.RouteSegment).HasColumnName("route_segment").HasMaxLength(64);
            entity.Property(type => type.SortOrder).HasColumnName("sort_order");

            // Seeded from the compiled registry rather than by the importer, so
            // the constraint exists from the moment the schema does. A type
            // added to the registry needs a migration; that is the intended
            // friction, because the registry is also what guards the {type}
            // route value.
            entity.HasData(ContentTypeRegistry.All.Select((definition, index) => new ContentTypeRow
            {
                Key = definition.Key,
                DisplayName = definition.DisplayName,
                PluralName = definition.PluralName,
                RouteSegment = definition.RouteSegment,
                SortOrder = index,
            }));
        });

    private static void ConfigureContentItems(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ContentItemRow>(entity =>
        {
            entity.ToTable("content_item");
            entity.HasKey(item => item.Id);

            entity.Property(item => item.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(64);
            entity.Property(item => item.ItemKey).HasColumnName("item_key").HasMaxLength(ContentSlug.MaxLength);
            entity.Property(item => item.Name).HasColumnName("name").HasMaxLength(512);
            entity.Property(item => item.SourceKey).HasColumnName("source_key").HasMaxLength(ContentSlug.MaxLength);
            entity.Property(item => item.ContentSet).HasColumnName("content_set").HasMaxLength(64);
            entity.Property(item => item.Summary).HasColumnName("summary");
            entity.Property(item => item.Facets).HasColumnName("facets").HasColumnType("jsonb");
            entity.Property(item => item.Body).HasColumnName("body").HasColumnType("jsonb");
            entity.Property(item => item.SearchText).HasColumnName("search_text");
            entity.Property(item => item.Version).HasColumnName("version").HasMaxLength(64);
            entity.Property(item => item.NameLower).HasColumnName("name_lower").HasMaxLength(512);
            entity.Property(item => item.SearchTextLower).HasColumnName("search_text_lower");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne<ContentTypeRow>()
                  .WithMany()
                  .HasForeignKey(item => item.ContentType)
                  .HasPrincipalKey(type => type.Key)
                  .OnDelete(DeleteBehavior.Restrict);

            // The real identity of a content item. Unique rather than the
            // primary key so references have a narrow integer to point at, but
            // it is this constraint that makes a double import impossible
            // rather than merely unlikely.
            entity.HasIndex(item => new { item.ContentType, item.ItemKey })
                  .IsUnique()
                  .HasDatabaseName("ix_content_item_type_key");

            // The list query's default ordering, covered end to end: filter by
            // type, order by folded name then key. On the folded copy rather
            // than on `name`, because that is the column the ORDER BY names —
            // an index on `name` would be ignored by the very query it exists
            // for. item_key is included so the tiebreaker does not force a
            // sort of its own.
            entity.HasIndex(item => new { item.ContentType, item.NameLower, item.ItemKey })
                  .HasDatabaseName("ix_content_item_type_name");

            // The two optional list filters. Partial, because most of the
            // corpus has a value and the rows that do not are never selected by
            // an equality predicate on it.
            entity.HasIndex(item => new { item.ContentType, item.SourceKey })
                  .HasDatabaseName("ix_content_item_type_source")
                  .HasFilter("source_key IS NOT NULL");

            entity.HasIndex(item => new { item.ContentType, item.ContentSet })
                  .HasDatabaseName("ix_content_item_type_content_set")
                  .HasFilter("content_set IS NOT NULL");

            // Substring search. GIN over trigrams is the only index shape that
            // serves a leading-wildcard LIKE, and both of these columns are
            // only ever queried that way.
            entity.HasIndex(item => item.NameLower)
                  .HasDatabaseName("ix_content_item_name_lower_trgm")
                  .HasMethod("gin")
                  .HasOperators("gin_trgm_ops");

            entity.HasIndex(item => item.SearchTextLower)
                  .HasDatabaseName("ix_content_item_search_text_trgm")
                  .HasMethod("gin")
                  .HasOperators("gin_trgm_ops");

            // The slug format the JSON Schemas fix, enforced by the database as
            // well as by the importer. The {key} route value is matched against
            // this pattern before any store is asked anything; having the same
            // rule at the far end means a row written by a future authoring API
            // or by hand cannot become unreachable through the API that is
            // supposed to serve it.
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "ck_content_item_key_slug",
                    "item_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");

                // The API hands the document body straight to the serialiser as
                // an object. A row whose body is an array, a number or a bare
                // string would produce a response no client's type for this
                // endpoint can hold, and the failure would surface at the far
                // end as a deserialisation error with nothing pointing back
                // here. The file-backed scanner refuses the same thing.
                table.HasCheckConstraint(
                    "ck_content_item_body_is_object",
                    "jsonb_typeof(body) = 'object'");
            });
        });

    private static void ConfigureContentReferences(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ContentReferenceRow>(entity =>
        {
            entity.ToTable("content_reference");
            entity.HasKey(reference => reference.Id);

            entity.Property(reference => reference.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.Property(reference => reference.FromItemId).HasColumnName("from_item_id");
            entity.Property(reference => reference.Relation).HasColumnName("relation").HasMaxLength(64);
            entity.Property(reference => reference.JsonPath).HasColumnName("json_path").HasMaxLength(256);
            entity.Property(reference => reference.TargetType).HasColumnName("target_type").HasMaxLength(64);
            entity.Property(reference => reference.TargetIdentifier).HasColumnName("target_identifier").HasMaxLength(512);
            entity.Property(reference => reference.ResolvedItemId).HasColumnName("resolved_item_id");
            entity.Property(reference => reference.Ordinal).HasColumnName("ordinal");

            // Stored as the text of the enum member rather than its ordinal.
            // An ordinal is unreadable in psql and silently changes meaning if
            // a member is ever inserted into the middle of the enum.
            entity.Property(reference => reference.TargetKind)
                  .HasColumnName("target_kind")
                  .HasMaxLength(16)
                  .HasConversion<string>();

            // Edges belong to the item they were read out of and have no
            // meaning without it, so re-importing an item can delete and
            // rewrite its edges without touching anything else.
            entity.HasOne(reference => reference.FromItem)
                  .WithMany(item => item.References)
                  .HasForeignKey(reference => reference.FromItemId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Deleting a target must not delete the edge that pointed at it:
            // the edge becomes unresolved, which is exactly what it was before
            // the target was authored, and is the state the importer knows how
            // to report and to re-resolve later.
            entity.HasOne(reference => reference.ResolvedItem)
                  .WithMany()
                  .HasForeignKey(reference => reference.ResolvedItemId)
                  .OnDelete(DeleteBehavior.SetNull);

            // One edge per field occurrence. Without this, a partially failed
            // import that reran would double every reference, and every
            // traversal would return each neighbour twice.
            entity.HasIndex(reference => new { reference.FromItemId, reference.Relation, reference.JsonPath })
                  .IsUnique()
                  .HasDatabaseName("ix_content_reference_from_path");

            // Reverse traversal: "what refers to this item". This is the
            // direction the print pipeline walks — given a source or a species,
            // collect everything that points at it.
            entity.HasIndex(reference => new { reference.ResolvedItemId, reference.Relation })
                  .HasDatabaseName("ix_content_reference_resolved")
                  .HasFilter("resolved_item_id IS NOT NULL");

            // Finding unresolved intent, which is the "what is this corpus
            // missing" report. Partial so it indexes only the rows that need
            // attention.
            entity.HasIndex(reference => new { reference.TargetType, reference.TargetIdentifier })
                  .HasDatabaseName("ix_content_reference_unresolved")
                  .HasFilter("resolved_item_id IS NULL");
        });

    private static void ConfigureContentRevisions(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ContentRevisionRow>(entity =>
        {
            entity.ToTable("content_revision");
            entity.HasKey(revision => revision.Id);

            entity.Property(revision => revision.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.Property(revision => revision.ContentType).HasColumnName("content_type").HasMaxLength(64);
            entity.Property(revision => revision.ItemKey).HasColumnName("item_key").HasMaxLength(ContentSlug.MaxLength);
            entity.Property(revision => revision.Number).HasColumnName("number");
            entity.Property(revision => revision.Name).HasColumnName("name").HasMaxLength(512);
            entity.Property(revision => revision.Body).HasColumnName("body").HasColumnType("jsonb");
            entity.Property(revision => revision.Version).HasColumnName("version").HasMaxLength(64);
            entity.Property(revision => revision.Action).HasColumnName("action").HasMaxLength(16);
            entity.Property(revision => revision.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(revision => revision.Reason)
                  .HasColumnName("reason")
                  .HasMaxLength(ContentAuthoringLimits.MaxReasonLength);
            entity.Property(revision => revision.SchemaVersion).HasColumnName("schema_version");
            entity.Property(revision => revision.RevertedFromId).HasColumnName("reverted_from_id");
            entity.Property(revision => revision.CreatedAt).HasColumnName("created_at");

            // Constrained to a registered type for the same reason the
            // catalogue is: a revision filed under a type the API never asks
            // about is a record nobody will ever find.
            entity.HasOne<ContentTypeRow>()
                  .WithMany()
                  .HasForeignKey(revision => revision.ContentType)
                  .HasPrincipalKey(type => type.Key)
                  .OnDelete(DeleteBehavior.Restrict);

            // Deliberately no foreign key to content_item: the history has to
            // survive the document being withdrawn, which is when it is worth
            // most.

            // The numbering guarantee. Two publishes racing on the same
            // document both read the same highest number and both try to write
            // it; this constraint makes the loser fail and retry rather than
            // produce two revision 4s, which would make the history ambiguous
            // in the one place it must not be.
            entity.HasIndex(revision => new { revision.ContentType, revision.ItemKey, revision.Number })
                  .IsUnique()
                  .HasDatabaseName("ix_content_revision_item_number");

            // Reading one document's history, newest first.
            entity.HasIndex(revision => new { revision.ContentType, revision.ItemKey, revision.CreatedAt })
                  .HasDatabaseName("ix_content_revision_item_time");

            // "What has this person changed", which is the question an audit
            // asks and the one a compromised account makes urgent.
            entity.HasIndex(revision => new { revision.ActorUserId, revision.CreatedAt })
                  .HasDatabaseName("ix_content_revision_actor")
                  .HasFilter("actor_user_id IS NOT NULL");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "ck_content_revision_key_slug",
                    "item_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");

                table.HasCheckConstraint(
                    "ck_content_revision_body_is_object",
                    "jsonb_typeof(body) = 'object'");

                table.HasCheckConstraint(
                    "ck_content_revision_number_positive",
                    "number >= 1");
            });
        });

    private static void ConfigureContentDrafts(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ContentDraftRow>(entity =>
        {
            entity.ToTable("content_draft");
            entity.HasKey(draft => draft.Id);

            entity.Property(draft => draft.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.Property(draft => draft.ContentType).HasColumnName("content_type").HasMaxLength(64);
            entity.Property(draft => draft.ItemKey).HasColumnName("item_key").HasMaxLength(ContentSlug.MaxLength);
            entity.Property(draft => draft.Name).HasColumnName("name").HasMaxLength(512);
            entity.Property(draft => draft.Body).HasColumnName("body").HasColumnType("jsonb");
            entity.Property(draft => draft.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(draft => draft.UpdatedByUserId).HasColumnName("updated_by_user_id");
            entity.Property(draft => draft.BaseRevisionId).HasColumnName("base_revision_id");
            entity.Property(draft => draft.ResolvesFlagId).HasColumnName("resolves_flag_id");
            entity.Property(draft => draft.CreatedAt).HasColumnName("created_at");
            entity.Property(draft => draft.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne<ContentTypeRow>()
                  .WithMany()
                  .HasForeignKey(draft => draft.ContentType)
                  .HasPrincipalKey(type => type.Key)
                  .OnDelete(DeleteBehavior.Restrict);

            // One draft per document. Two people editing the same entry collide
            // here, visibly, instead of each silently discarding the other's
            // work at publication.
            entity.HasIndex(draft => new { draft.ContentType, draft.ItemKey })
                  .IsUnique()
                  .HasDatabaseName("ix_content_draft_item");

            // The worklist ordering: most recently touched first.
            entity.HasIndex(draft => draft.UpdatedAt)
                  .HasDatabaseName("ix_content_draft_updated");

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "ck_content_draft_key_slug",
                    "item_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");

                table.HasCheckConstraint(
                    "ck_content_draft_body_is_object",
                    "jsonb_typeof(body) = 'object'");
            });
        });
}
