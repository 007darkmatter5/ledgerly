using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerly.Data.Migrations
{
    /// <inheritdoc />
    public partial class LedgerSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LedgerMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LedgerId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    InvitedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    Nickname = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LedgerMembers_Ledgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerMembers_LedgerId_UserId",
                table: "LedgerMembers",
                columns: new[] { "LedgerId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerMembers_UserId",
                table: "LedgerMembers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerMembers");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "AspNetUsers");
        }
    }
}
