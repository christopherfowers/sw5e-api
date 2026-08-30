using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialContentSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "content_type",
                schema: "content",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    plural_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    route_segment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_type", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "content_item",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    item_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    source_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    content_set = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    summary = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    facets = table.Column<string>(type: "jsonb", nullable: false),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    name_lower = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    search_text_lower = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_item", x => x.id);
                    table.CheckConstraint("ck_content_item_body_is_object", "jsonb_typeof(body) = 'object'");
                    table.CheckConstraint("ck_content_item_key_slug", "item_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.ForeignKey(
                        name: "FK_content_item_content_type_content_type",
                        column: x => x.content_type,
                        principalSchema: "content",
                        principalTable: "content_type",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_reference",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    from_item_id = table.Column<long>(type: "bigint", nullable: false),
                    relation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    json_path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    target_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    resolved_item_id = table.Column<long>(type: "bigint", nullable: true),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_reference", x => x.id);
                    table.ForeignKey(
                        name: "FK_content_reference_content_item_from_item_id",
                        column: x => x.from_item_id,
                        principalSchema: "content",
                        principalTable: "content_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_content_reference_content_item_resolved_item_id",
                        column: x => x.resolved_item_id,
                        principalSchema: "content",
                        principalTable: "content_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                schema: "content",
                table: "content_type",
                columns: new[] { "key", "display_name", "plural_name", "route_segment", "sort_order" },
                values: new object[,]
                {
                    { "archetype", "Archetype", "Archetypes", "archetypes", 3 },
                    { "background", "Background", "Backgrounds", "backgrounds", 2 },
                    { "equipment", "Equipment", "Equipment", "equipment", 7 },
                    { "feat", "Feat", "Feats", "feats", 5 },
                    { "feature", "Feature", "Features", "features", 4 },
                    { "monster", "Monster", "Monsters", "monsters", 8 },
                    { "power", "Power", "Powers", "powers", 6 },
                    { "source", "Source", "Sources", "sources", 0 },
                    { "species", "Species", "Species", "species", 1 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_item_name_lower_trgm",
                schema: "content",
                table: "content_item",
                column: "name_lower")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_content_item_search_text_trgm",
                schema: "content",
                table: "content_item",
                column: "search_text_lower")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_content_item_type_content_set",
                schema: "content",
                table: "content_item",
                columns: new[] { "content_type", "content_set" },
                filter: "content_set IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_content_item_type_key",
                schema: "content",
                table: "content_item",
                columns: new[] { "content_type", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_item_type_name",
                schema: "content",
                table: "content_item",
                columns: new[] { "content_type", "name_lower", "item_key" });

            migrationBuilder.CreateIndex(
                name: "ix_content_item_type_source",
                schema: "content",
                table: "content_item",
                columns: new[] { "content_type", "source_key" },
                filter: "source_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_content_reference_from_path",
                schema: "content",
                table: "content_reference",
                columns: new[] { "from_item_id", "relation", "json_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_reference_resolved",
                schema: "content",
                table: "content_reference",
                columns: new[] { "resolved_item_id", "relation" },
                filter: "resolved_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_content_reference_unresolved",
                schema: "content",
                table: "content_reference",
                columns: new[] { "target_type", "target_identifier" },
                filter: "resolved_item_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_reference",
                schema: "content");

            migrationBuilder.DropTable(
                name: "content_item",
                schema: "content");

            migrationBuilder.DropTable(
                name: "content_type",
                schema: "content");
        }
    }
}
