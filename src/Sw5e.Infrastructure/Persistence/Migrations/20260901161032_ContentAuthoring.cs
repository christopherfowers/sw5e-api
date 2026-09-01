using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the two tables content authoring stands on: the append-only history
    /// and the drafts that are not yet live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither table is read by the content read path. That is the point: the
    /// site the community uses queries exactly the tables, with exactly the
    /// predicates, that it did before this migration ran, so nothing about
    /// adding authoring can change what a reader sees or how fast they see it.
    /// </para>
    /// <para>
    /// The trigger installed at the end is the part that cannot be expressed in
    /// the model, and it is the reason this migration is worth reading. See its
    /// own comment below.
    /// </para>
    /// </remarks>
    public partial class ContentAuthoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_draft",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    item_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_revision_id = table.Column<long>(type: "bigint", nullable: true),
                    resolves_flag_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_draft", x => x.id);
                    table.CheckConstraint("ck_content_draft_body_is_object", "jsonb_typeof(body) = 'object'");
                    table.CheckConstraint("ck_content_draft_key_slug", "item_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.ForeignKey(
                        name: "FK_content_draft_content_type_content_type",
                        column: x => x.content_type,
                        principalSchema: "content",
                        principalTable: "content_type",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_revision",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    item_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    reverted_from_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_revision", x => x.id);
                    table.CheckConstraint("ck_content_revision_body_is_object", "jsonb_typeof(body) = 'object'");
                    table.CheckConstraint("ck_content_revision_key_slug", "item_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_content_revision_number_positive", "number >= 1");
                    table.ForeignKey(
                        name: "FK_content_revision_content_type_content_type",
                        column: x => x.content_type,
                        principalSchema: "content",
                        principalTable: "content_type",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_draft_item",
                schema: "content",
                table: "content_draft",
                columns: new[] { "content_type", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_draft_updated",
                schema: "content",
                table: "content_draft",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_content_revision_actor",
                schema: "content",
                table: "content_revision",
                columns: new[] { "actor_user_id", "created_at" },
                filter: "actor_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_content_revision_item_number",
                schema: "content",
                table: "content_revision",
                columns: new[] { "content_type", "item_key", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_revision_item_time",
                schema: "content",
                table: "content_revision",
                columns: new[] { "content_type", "item_key", "created_at" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table takes the trigger with it, but not the
            // function: a trigger function outlives every trigger that used it,
            // and one left behind would collide with the CREATE above if this
            // migration were ever reapplied. The trigger is dropped explicitly
            // as well so the two statements read as one undo rather than
            // relying on a cascade to cover half of it.
            //
            // DROP TABLE does not fire row-level triggers, so the append-only
            // guard does not stand in the way of its own removal.
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS content_revision_append_only ON content.content_revision;");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS content.content_revision_append_only();");

            migrationBuilder.DropTable(
                name: "content_draft",
                schema: "content");

            migrationBuilder.DropTable(
                name: "content_revision",
                schema: "content");
        }
    }
}
