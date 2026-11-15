using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperBackend.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDisabled",
                schema: "identity",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "RoleId",
                schema: "identity",
                table: "AccessAudits",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDisabled",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<string>(
                name: "RoleId",
                schema: "identity",
                table: "AccessAudits",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
