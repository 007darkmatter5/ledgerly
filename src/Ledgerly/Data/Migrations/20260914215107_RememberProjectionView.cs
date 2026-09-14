using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerly.Data.Migrations
{
    /// <inheritdoc />
    public partial class RememberProjectionView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProjectionAccountIds",
                table: "Ledgers",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectionDays",
                table: "Ledgers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ProjectionWorstCase",
                table: "Ledgers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProjectionAccountIds",
                table: "Ledgers");

            migrationBuilder.DropColumn(
                name: "ProjectionDays",
                table: "Ledgers");

            migrationBuilder.DropColumn(
                name: "ProjectionWorstCase",
                table: "Ledgers");
        }
    }
}
