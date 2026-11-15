using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Notes.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteCreationStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteCreationStats",
                schema: "notes",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TotalCreated = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteCreationStats", x => x.OwnerId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteCreationStats",
                schema: "notes");
        }
    }
}
