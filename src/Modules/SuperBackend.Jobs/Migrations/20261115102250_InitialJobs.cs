using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Jobs.Migrations
{
    /// <inheritdoc />
    public partial class InitialJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "jobs");

            migrationBuilder.CreateTable(
                name: "Jobs",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.CheckConstraint("CK_Jobs_Attempts", "\"Attempts\" >= 0 AND \"MaxAttempts\" BETWEEN 1 AND 20 AND \"Attempts\" <= \"MaxAttempts\"");
                    table.CheckConstraint("CK_Jobs_Status", "\"Status\" IN ('pending','running','succeeded','dead')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_AvailableAt_Id",
                schema: "jobs",
                table: "Jobs",
                columns: new[] { "Status", "AvailableAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_LeaseUntil",
                schema: "jobs",
                table: "Jobs",
                columns: new[] { "Status", "LeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs",
                schema: "jobs");
        }
    }
}
