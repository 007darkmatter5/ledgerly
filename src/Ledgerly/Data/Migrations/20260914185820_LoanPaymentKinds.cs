using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerly.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoanPaymentKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoanPaymentKind",
                table: "Bills",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "MonthlyPayment");

            migrationBuilder.AddColumn<string>(
                name: "PaymentKind",
                table: "BillOccurrences",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoanPaymentKind",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "PaymentKind",
                table: "BillOccurrences");
        }
    }
}
