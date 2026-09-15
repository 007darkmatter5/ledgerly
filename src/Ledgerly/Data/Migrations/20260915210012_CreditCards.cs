using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerly.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreditCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CardAccountId",
                table: "Bills",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardPaymentRule",
                table: "Bills",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "StatementBalance");

            migrationBuilder.AddColumn<decimal>(
                name: "AprPercent",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditLimit",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumPaymentFloor",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumPaymentPercent",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlySpending",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StatementBalance",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatementDay",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bills_CardAccountId",
                table: "Bills",
                column: "CardAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_Accounts_CardAccountId",
                table: "Bills",
                column: "CardAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bills_Accounts_CardAccountId",
                table: "Bills");

            migrationBuilder.DropIndex(
                name: "IX_Bills_CardAccountId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "CardAccountId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "CardPaymentRule",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "AprPercent",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "CreditLimit",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MinimumPaymentFloor",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MinimumPaymentPercent",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MonthlySpending",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "StatementBalance",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "StatementDay",
                table: "Accounts");
        }
    }
}
