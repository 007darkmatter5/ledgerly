using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerly.Data.Migrations
{
    /// <inheritdoc />
    public partial class Payees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters: create payees and link bills and loans to them *before* the old text columns are dropped.
            migrationBuilder.CreateTable(
                name: "Payees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LedgerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    WebsiteUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payees_Ledgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payees_LedgerId_Name",
                table: "Payees",
                columns: new[] { "LedgerId", "Name" },
                unique: true);

            migrationBuilder.AddColumn<int>(
                name: "PayeeId",
                table: "Bills",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LenderId",
                table: "Loans",
                type: "INTEGER",
                nullable: true);

            // The payee name each bill and loan will get: its trimmed payee/lender text (cut to 100 characters).
            // A loan with a lender website but no lender name gets "<loan name> lender" so the website isn't lost.
            // Column references are qualified with the table: inside a subquery on "Payees", a bare "Name" would mean the payee's name.
            static string BillPayeeName(string bills) => $"""trim(substr(trim({bills}."Payee"), 1, 100))""";
            static string LoanLenderName(string loans) => $"""
                CASE WHEN {loans}."Lender" IS NOT NULL AND trim({loans}."Lender") <> '' THEN trim(substr(trim({loans}."Lender"), 1, 100))
                     WHEN {loans}."LenderUrl" IS NOT NULL THEN trim(substr(trim({loans}."Name"), 1, 93)) || ' lender'
                END
                """;

            // One payee per distinct name in each ledger, across bill payees and loan lenders (ignoring case).
            migrationBuilder.Sql($"""
                INSERT INTO "Payees" ("LedgerId", "Name")
                SELECT "LedgerId", MIN("PayeeName")
                FROM (
                    SELECT "LedgerId", {BillPayeeName("\"Bills\"")} AS "PayeeName" FROM "Bills"
                    WHERE "Payee" IS NOT NULL AND trim("Payee") <> ''
                    UNION ALL
                    SELECT "LedgerId", {LoanLenderName("\"Loans\"")} FROM "Loans"
                )
                WHERE "PayeeName" IS NOT NULL AND "PayeeName" <> ''
                GROUP BY "LedgerId", "PayeeName" COLLATE NOCASE;
                """);

            // Lender websites move to the payee (the first loan's, if loans with the same lender differ).
            migrationBuilder.Sql($"""
                UPDATE "Payees"
                SET "WebsiteUrl" = (
                    SELECT l."LenderUrl" FROM "Loans" l
                    WHERE l."LedgerId" = "Payees"."LedgerId" AND l."LenderUrl" IS NOT NULL
                      AND "Payees"."Name" = {LoanLenderName("l")}
                    ORDER BY l."Id" LIMIT 1);
                """);

            migrationBuilder.Sql($"""
                UPDATE "Bills"
                SET "PayeeId" = (SELECT p."Id" FROM "Payees" p WHERE p."LedgerId" = "Bills"."LedgerId" AND p."Name" = {BillPayeeName("\"Bills\"")})
                WHERE "Payee" IS NOT NULL AND trim("Payee") <> '';
                """);

            migrationBuilder.Sql($"""
                UPDATE "Loans"
                SET "LenderId" = (SELECT p."Id" FROM "Payees" p WHERE p."LedgerId" = "Loans"."LedgerId" AND p."Name" = {LoanLenderName("\"Loans\"")});
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Bills_PayeeId",
                table: "Bills",
                column: "PayeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_LenderId",
                table: "Loans",
                column: "LenderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_Payees_PayeeId",
                table: "Bills",
                column: "PayeeId",
                principalTable: "Payees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Loans_Payees_LenderId",
                table: "Loans",
                column: "LenderId",
                principalTable: "Payees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.DropColumn(
                name: "Payee",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "Lender",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "LenderUrl",
                table: "Loans");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bills_Payees_PayeeId",
                table: "Bills");

            migrationBuilder.DropForeignKey(
                name: "FK_Loans_Payees_LenderId",
                table: "Loans");

            migrationBuilder.DropTable(
                name: "Payees");

            migrationBuilder.DropIndex(
                name: "IX_Loans_LenderId",
                table: "Loans");

            migrationBuilder.DropIndex(
                name: "IX_Bills_PayeeId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "LenderId",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "PayeeId",
                table: "Bills");

            migrationBuilder.AddColumn<string>(
                name: "Lender",
                table: "Loans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LenderUrl",
                table: "Loans",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payee",
                table: "Bills",
                type: "TEXT",
                nullable: true);
        }
    }
}
