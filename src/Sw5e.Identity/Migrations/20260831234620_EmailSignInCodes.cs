using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sw5e.Identity.Migrations
{
    /// <inheritdoc />
    public partial class EmailSignInCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailSignInCodes",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CodeSalt = table.Column<byte[]>(type: "bytea", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSignInCodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSignInCodes_Address",
                schema: "identity",
                table: "EmailSignInCodes",
                columns: new[] { "NormalizedEmail", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailSignInCodes",
                schema: "identity");
        }
    }
}
