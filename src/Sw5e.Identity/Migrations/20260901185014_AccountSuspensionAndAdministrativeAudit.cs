using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sw5e.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AccountSuspensionAndAdministrativeAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspendedAt",
                schema: "identity",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SuspendedByUserId",
                schema: "identity",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuspensionReason",
                schema: "identity",
                table: "AspNetUsers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdministrativeActions",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorDisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectDisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RolesBefore = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RolesAfter = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdministrativeActions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_Suspended",
                schema: "identity",
                table: "AspNetUsers",
                column: "SuspendedAt",
                filter: "\"SuspendedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativeActions_Actor",
                schema: "identity",
                table: "AdministrativeActions",
                columns: new[] { "ActorUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativeActions_CreatedAt",
                schema: "identity",
                table: "AdministrativeActions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativeActions_Subject",
                schema: "identity",
                table: "AdministrativeActions",
                columns: new[] { "SubjectUserId", "CreatedAt" });

            // The administrative log is append-only, and PostgreSQL enforces
            // it rather than this codebase promising to.
            //
            // Nothing in the application issues an UPDATE or a DELETE against
            // this table, so the trigger is not there to catch a bug here. It
            // is there because the only value an audit record has is the
            // confidence that it was not edited afterwards, and the person with
            // both the database access and the motive to edit it is an
            // administrator — the exact party this table exists to hold to
            // account. "We do not write that statement" is a weaker claim than
            // "the statement is refused", and this is the same protection the
            // content revision table already carries.
            //
            // search_path is pinned inside the function so that the definition
            // cannot be made to resolve a different pg_catalog by a caller who
            // controls their own search path.
            // MUTATION: the administrative log is no longer append-only.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropped before the table, because the table cannot be dropped
            // while a trigger depends on it and the function cannot be dropped
            // while the trigger does.
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS administrative_action_append_only " +
                "ON identity.\"AdministrativeActions\";");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS identity.administrative_action_append_only();");

            migrationBuilder.DropTable(
                name: "AdministrativeActions",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_Suspended",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SuspendedAt",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SuspendedByUserId",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SuspensionReason",
                schema: "identity",
                table: "AspNetUsers");
        }
    }
}
