using Microsoft.EntityFrameworkCore;

namespace Sw5e.Infrastructure.Persistence.Notifications;

/// <summary>
/// What each account has been told, and whether they have read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fourth context, and a fourth schema.</b> Content is bulk-imported from
/// a reviewed repository and rebuilt wholesale on every deploy. Identity is
/// credentials. Moderation is prose written at runtime by people who are not
/// trusted. This is none of those: it is a per-account inbox, written by the
/// platform itself about events in all three of the others.
/// </para>
/// <para>
/// Putting it in the content schema would have been the shortcut and would have
/// been a data-loss bug, for exactly the reason the moderation tables are kept
/// out of there. The importer's job is to make the content tables match the
/// repository, and a table of things people have been told, sitting among them,
/// is one careless truncate away from gone.
/// </para>
/// <para>
/// User ids are carried without foreign keys across the schema boundary. A
/// constraint there would make identity unrestorable without this, and this
/// unrestorable without identity, which is the opposite of what separate
/// pipelines are for. The cost is that a deleted account can leave rows behind;
/// the sweep that prunes read notifications is where those go.
/// </para>
/// </remarks>
public sealed class Sw5eNotificationDbContext(DbContextOptions<Sw5eNotificationDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "notifications";

    /// <summary>
    /// This schema's own migration history.
    /// </summary>
    /// <remarks>
    /// Inside this schema so it cannot collide with another context's. Two
    /// contexts sharing one history table each read the other's rows as
    /// migrations of their own that have already run, and the first Migrate
    /// after that tries to create tables which already exist.
    /// </remarks>
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<NotificationRow>(entity =>
        {
            entity.ToTable("notification");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.UserId).HasColumnName("user_id");

            entity.Property(row => row.Kind)
                  .HasColumnName("kind")
                  .HasMaxLength(64)
                  .IsRequired();

            entity.Property(row => row.ActorUserId).HasColumnName("actor_user_id");

            entity.Property(row => row.SubjectType)
                  .HasColumnName("subject_type")
                  .HasMaxLength(64);

            entity.Property(row => row.SubjectKey)
                  .HasColumnName("subject_key")
                  .HasMaxLength(Domain.Content.ContentSlug.MaxLength);

            entity.Property(row => row.Detail)
                  .HasColumnName("detail")
                  .HasColumnType("jsonb")
                  .IsRequired();

            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.ReadAt).HasColumnName("read_at");

            /*
              The index the bell runs on.

              Every page view asks one question: how many unread does this
              account have. Filtered on read_at being null so the index holds
              only what the count is over, which on an account that reads its
              notifications is almost nothing, and never grows with history.
            */
            entity.HasIndex(row => row.UserId)
                  .HasDatabaseName("ix_notification_unread")
                  .HasFilter("read_at IS NULL");

            // The feed, newest first, for one account.
            entity.HasIndex(row => new { row.UserId, row.CreatedAt })
                  .HasDatabaseName("ix_notification_user_created");

            // The retention sweep, which deletes read rows past their keep.
            entity.HasIndex(row => row.ReadAt)
                  .HasDatabaseName("ix_notification_read_at")
                  .HasFilter("read_at IS NOT NULL");
        });
    }
}
