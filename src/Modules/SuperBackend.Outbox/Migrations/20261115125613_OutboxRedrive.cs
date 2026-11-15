using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Outbox.Migrations
{
    /// <inheritdoc />
    public partial class OutboxRedrive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplacementId",
                schema: "outbox",
                table: "Messages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacementId",
                schema: "outbox",
                table: "Messages");
        }
    }
}
