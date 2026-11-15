using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Identity.Migrations
{
    /// <inheritdoc />
    public partial class SessionSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reason",
                schema: "identity",
                table: "AccessAudits",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                schema: "identity",
                table: "AccessAudits",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FederationHandoffs",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ExternalKey = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederationHandoffs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FederationHandoffs_ExpiresAt",
                schema: "identity",
                table: "FederationHandoffs",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ExpiresAt",
                schema: "identity",
                table: "Sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                schema: "identity",
                table: "Sessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FederationHandoffs",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "identity",
                table: "AccessAudits");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "identity",
                table: "AccessAudits");
        }
    }
}
