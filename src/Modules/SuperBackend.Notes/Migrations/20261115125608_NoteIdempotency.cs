using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Notes.Migrations
{
    /// <inheritdoc />
    public partial class NoteIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteCreateReceipts",
                schema: "notes",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteCreateReceipts", x => new { x.OwnerId, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteCreateReceipts_CreatedAt",
                schema: "notes",
                table: "NoteCreateReceipts",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteCreateReceipts",
                schema: "notes");
        }
    }
}
