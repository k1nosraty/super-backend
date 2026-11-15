using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Outbox.Migrations
{
    /// <inheritdoc />
    public partial class InitialOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "outbox");

            migrationBuilder.CreateTable(
                name: "InboxReceipts",
                schema: "outbox",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxReceipts", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                schema: "outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConflictAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.CheckConstraint("CK_Messages_MaxAttempts", "\"MaxAttempts\" BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_Messages_TerminalState", "\"DispatchedAt\" IS NULL OR \"ConflictAt\" IS NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CreatedAt_Id",
                schema: "outbox",
                table: "Messages",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"DispatchedAt\" IS NULL AND \"ConflictAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxReceipts",
                schema: "outbox");

            migrationBuilder.DropTable(
                name: "Messages",
                schema: "outbox");
        }
    }
}
