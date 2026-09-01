using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sw5e.Infrastructure.Persistence.Moderation.Migrations
{
    /// <summary>
    /// Lets a resolved report name the content revision that resolved it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable, and no backfill. Every report already resolved was resolved
    /// before there was any way to change content through the platform, so
    /// there is no revision any of them could honestly point at. Inventing one
    /// would put a false statement into the moderation record to avoid a null,
    /// and a null here reads correctly: nobody recorded which change fixed it,
    /// because at the time nobody could.
    /// </para>
    /// <para>
    /// A plain bigint with no foreign key. The content schema lives in another
    /// PostgreSQL schema and may live in another database entirely — the same
    /// reason this table holds reporter accounts as bare uuids — so the
    /// constraint could not be declared without welding moderation to content.
    /// The handler verifies the revision exists before storing it.
    /// </para>
    /// </remarks>
    public partial class FlagResolvedByRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "resolved_by_revision_id",
                schema: "moderation",
                table: "content_flag",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resolved_by_revision_id",
                schema: "moderation",
                table: "content_flag");
        }
    }
}
