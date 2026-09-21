using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sw5e.Infrastructure.Persistence.Moderation.Migrations
{
    /// <inheritdoc />
    public partial class InitialModerationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "moderation");

            migrationBuilder.CreateTable(
                name: "content_flag",
                schema: "moderation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false, collation: "C"),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    target_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    target_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    reason = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false, collation: "C"),
                    details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    status = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false, collation: "C"),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewer_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_flag", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_flag_reporter",
                schema: "moderation",
                table: "content_flag",
                columns: new[] { "reporter_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_flag_status_created",
                schema: "moderation",
                table: "content_flag",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_flag_target",
                schema: "moderation",
                table: "content_flag",
                columns: new[] { "target_type", "target_key" });

            migrationBuilder.CreateIndex(
                name: "ux_content_flag_outstanding_per_reporter",
                schema: "moderation",
                table: "content_flag",
                columns: new[] { "reporter_user_id", "target_type", "target_key", "reason" },
                unique: true,
                filter: "\"status\" IN ('open', 'accepted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_flag",
                schema: "moderation");
        }
    }
}
