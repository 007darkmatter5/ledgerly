using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerly.Data.Migrations
{
    /// <inheritdoc />
    public partial class Transfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Transfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LedgerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FromAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAutomatic = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transfers_Accounts_FromAccountId",
                        column: x => x.FromAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Transfers_Accounts_ToAccountId",
                        column: x => x.ToAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Transfers_Ledgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TransferOccurrences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TransferId = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    CompletedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransferOccurrences_Transfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "Transfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferOccurrences_TransferId_ScheduledDate",
                table: "TransferOccurrences",
                columns: new[] { "TransferId", "ScheduledDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_FromAccountId",
                table: "Transfers",
                column: "FromAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_LedgerId",
                table: "Transfers",
                column: "LedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_ToAccountId",
                table: "Transfers",
                column: "ToAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransferOccurrences");

            migrationBuilder.DropTable(
                name: "Transfers");
        }
    }
}
