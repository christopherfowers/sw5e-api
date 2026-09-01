using Microsoft.EntityFrameworkCore;
using Sw5e.Domain.Moderation;

namespace Sw5e.Infrastructure.Persistence.Moderation;

/// <summary>
/// The reports readers raise against the reference, and the state of each.
/// </summary>
/// <remarks>
/// <para>
/// <b>A third context, and a third schema.</b> The platform already keeps
/// content and identity apart because they are written by different pipelines
/// and restored on different schedules. This is a third pipeline again, and it
/// resembles neither: content is bulk-imported from a reviewed repository and
/// is rebuilt wholesale on every deploy; identity is credentials; this is
/// user-submitted prose about content, written at runtime by people who are not
/// trusted.
/// </para>
/// <para>
/// Putting it in the content schema would have been the tempting shortcut and
/// would have been a data-loss bug. The importer's job is to make the content
/// tables match the repository; a table of user reports sitting among them is a
/// table one careless truncate away from gone, and the reports are the only
/// copy of knowledge — who drew this picture — that exists nowhere else. Its
/// own schema, with its own migration history, means the content importer
/// cannot reach it even by accident.
/// </para>
/// <para>
/// It has no foreign keys out of this schema at all. See
/// <see cref="ContentFlagRow.TargetType"/> and
/// <see cref="ContentFlagRow.ReporterUserId"/>: both point at rows owned by a
/// context that a deployment is free to move to another database, so the
/// integrity that matters is enforced where it can be — at the endpoint, before
/// the row is written — rather than by a constraint that would silently forbid
/// a supported deployment.
/// </para>
/// </remarks>
public sealed class Sw5eModerationDbContext(DbContextOptions<Sw5eModerationDbContext> options)
    : DbContext(options)
{
    /// <summary>The PostgreSQL schema every table in this context lives in.</summary>
    public const string SchemaName = "moderation";

    /// <summary>
    /// Migration history for this context, kept inside <see cref="SchemaName"/>.
    /// </summary>
    /// <remarks>
    /// Three contexts now share one database by default. Any two of them
    /// pointed at the same history table read each other's rows as migrations
    /// of their own that have already been applied, and the next deploy tries
    /// to create tables that exist. Each history table lives with the tables it
    /// describes for exactly that reason.
    /// </remarks>
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>Every report ever raised, in whatever state it reached.</summary>
    public DbSet<ContentFlagRow> ContentFlags => Set<ContentFlagRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<ContentFlagRow>(entity =>
        {
            entity.ToTable("content_flag");
            entity.HasKey(flag => flag.Id);

            entity.Property(flag => flag.Id).HasColumnName("id");

            // Stored as the published wire spelling rather than the enum's
            // ordinal or its member name. An ordinal is meaningless in psql and
            // changes what every existing row says the moment a member is
            // inserted into the middle of the enum; the member name makes a C#
            // rename a silent data migration. See FlagWire.
            entity.Property(flag => flag.TargetKind)
                  .HasColumnName("target_kind")
                  .HasMaxLength(FlagWire.MaxNameLength)
                  .HasConversion(
                      value => FlagWire.NameOf(value),
                      name => ParseTargetKind(name));

            entity.Property(flag => flag.TargetType)
                  .HasColumnName("target_type")
                  .HasMaxLength(64)
                  .IsRequired();

            entity.Property(flag => flag.TargetKey)
                  .HasColumnName("target_key")
                  .HasMaxLength(Domain.Content.ContentSlug.MaxLength)
                  .IsRequired();

            entity.Property(flag => flag.TargetName)
                  .HasColumnName("target_name")
                  .HasMaxLength(512)
                  .IsRequired();

            entity.Property(flag => flag.Reason)
                  .HasColumnName("reason")
                  .HasMaxLength(FlagWire.MaxNameLength)
                  .HasConversion(
                      value => FlagWire.NameOf(value),
                      name => ParseReason(name));

            entity.Property(flag => flag.Status)
                  .HasColumnName("status")
                  .HasMaxLength(FlagWire.MaxNameLength)
                  .HasConversion(
                      value => FlagWire.NameOf(value),
                      name => ParseStatus(name));

            // The length is a constraint rather than a second opinion. The
            // endpoint refuses anything longer with a 400 and a field error,
            // which is the good failure; this is what happens if a second write
            // path is ever added and forgets to.
            entity.Property(flag => flag.Details)
                  .HasColumnName("details")
                  .HasMaxLength(ContentFlagRules.MaxDetailsLength);

            entity.Property(flag => flag.ReporterUserId).HasColumnName("reporter_user_id");
            entity.Property(flag => flag.CreatedAt).HasColumnName("created_at");
            entity.Property(flag => flag.ReviewedByUserId).HasColumnName("reviewed_by_user_id");
            entity.Property(flag => flag.ReviewedAt).HasColumnName("reviewed_at");

            entity.Property(flag => flag.ReviewerNote)
                  .HasColumnName("reviewer_note")
                  .HasMaxLength(ContentFlagRules.MaxReviewerNoteLength);

            entity.Property(flag => flag.ResolvedByRevisionId)
                  .HasColumnName("resolved_by_revision_id");

            // The queue's own query: outstanding reports, newest first. Status
            // leads because every view of the queue filters on it, and without
            // this index the cheapest page a moderator can ask for is a scan of
            // every report the platform has ever received.
            entity.HasIndex(flag => new { flag.Status, flag.CreatedAt })
                  .HasDatabaseName("ix_content_flag_status_created");

            // "Everything raised against this document", which is what makes a
            // hundred and fifty attribution reports collapse into a list of the
            // pictures they are about rather than a hundred and fifty rows.
            entity.HasIndex(flag => new { flag.TargetType, flag.TargetKey })
                  .HasDatabaseName("ix_content_flag_target");

            // "What have I reported", and the per-account quota the submit
            // endpoint enforces, which counts this account's recent rows on
            // every submission.
            entity.HasIndex(flag => new { flag.ReporterUserId, flag.CreatedAt })
                  .HasDatabaseName("ix_content_flag_reporter");

            // One account may not have two outstanding reports of the same
            // reason against the same thing.
            //
            // This is spam control that a rate limit cannot provide: a limiter
            // slows a flood down, and this makes the flood pointless. It is
            // filtered on the outstanding states so that a report which was
            // declined a year ago does not permanently bar the same person from
            // raising it again when circumstances change — a decline is a
            // judgement, not a ban.
            //
            // A unique index and not a check in the handler, because two
            // requests from one account arriving together both find nothing and
            // both insert. This is the only place that race can be lost safely.
            entity.HasIndex(flag => new
                  {
                      flag.ReporterUserId,
                      flag.TargetType,
                      flag.TargetKey,
                      flag.Reason,
                  })
                  .IsUnique()
                  .HasFilter("\"status\" IN ('open', 'accepted')")
                  .HasDatabaseName("ux_content_flag_outstanding_per_reporter");

            ApplyByteOrderCollation(entity.Metadata);
        });
    }

    /// <summary>
    /// Reads a value the database wrote, and refuses to invent one it did not.
    /// </summary>
    /// <remarks>
    /// A row whose status column holds a string this build does not recognise
    /// is a row written by a newer build, and there is exactly one safe thing
    /// to do with it: stop. Defaulting to <c>Open</c> would silently reopen
    /// resolved reports on a rollback; defaulting to <c>Resolved</c> would
    /// silently close live ones. Throwing surfaces the version skew as a loud
    /// failure on a moderator's page rather than as a queue that quietly lies.
    /// </remarks>
    private static FlagTargetKind ParseTargetKind(string name) =>
        FlagWire.TryParseTargetKind(name, out var value)
            ? value
            : throw Unrecognised(name, nameof(FlagTargetKind));

    private static FlagReason ParseReason(string name) =>
        FlagWire.TryParseReason(name, out var value)
            ? value
            : throw Unrecognised(name, nameof(FlagReason));

    private static FlagStatus ParseStatus(string name) =>
        FlagWire.TryParseStatus(name, out var value)
            ? value
            : throw Unrecognised(name, nameof(FlagStatus));

    private static InvalidOperationException Unrecognised(string name, string typeName) =>
        new($"The moderation store holds '{name}', which this build does not recognise as a " +
            $"{typeName}. That means the schema was written by a newer build than this one.");

    /// <summary>
    /// Pins every text column here to the <c>C</c> collation, matching the
    /// content schema.
    /// </summary>
    /// <remarks>
    /// The reason is narrower than it is over there and still real: the unique
    /// index above decides whether two reports are the same report, and it
    /// decides it by comparing text. Under a locale collation, whether two keys
    /// or two reason names are equal depends on the locale the database was
    /// initialised with — so duplicate suppression would behave differently on
    /// a developer's machine and in production, which is precisely the class of
    /// difference nobody finds until it matters.
    /// </remarks>
    private static void ApplyByteOrderCollation(Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entityType)
    {
        foreach (var property in entityType.GetProperties())
        {
            var storedAs =
                property.GetValueConverter()?.ProviderClrType
                ?? property.GetProviderClrType()
                ?? property.ClrType;

            if (storedAs == typeof(string))
            {
                property.SetCollation("C");
            }
        }
    }
}
