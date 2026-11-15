using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Tenancy.Migrations
{
    /// <inheritdoc />
    public partial class TenantLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "tenancy",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                schema: "tenancy",
                table: "Documents",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "tenancy",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "tenancy",
                table: "Documents");
        }
    }
}
