using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerly.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters: create categories and link bills to them *before* the old text column is dropped.
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LedgerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "NOCASE"),
                    Color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Categories_Ledgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_LedgerId_Name",
                table: "Categories",
                columns: new[] { "LedgerId", "Name" },
                unique: true);

            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "Bills",
                type: "INTEGER",
                nullable: true);

            // One category per distinct name (ignoring case and surrounding spaces) in each ledger. Names are cut
            // to the new 50-character limit; when spellings differ only in case, the capitalized one is kept.
            migrationBuilder.Sql("""
                INSERT INTO "Categories" ("LedgerId", "Name")
                SELECT "LedgerId", MIN(trim(substr(trim("Category"), 1, 50)))
                FROM "Bills"
                WHERE "Category" IS NOT NULL AND trim("Category") <> ''
                GROUP BY "LedgerId", trim(substr(trim("Category"), 1, 50)) COLLATE NOCASE;
                """);

            // Give migrated categories distinct colors from the app's palette (see CategoryChip.Palette).
            migrationBuilder.Sql("""
                UPDATE "Categories"
                SET "Color" = CASE ("Id" - 1) % 12
                    WHEN 0 THEN '#2E7D5B' WHEN 1 THEN '#3F6FB5' WHEN 2 THEN '#F39C12' WHEN 3 THEN '#8E44AD'
                    WHEN 4 THEN '#16A085' WHEN 5 THEN '#E91E63' WHEN 6 THEN '#C0392B' WHEN 7 THEN '#5C6BC0'
                    WHEN 8 THEN '#D35400' WHEN 9 THEN '#7CB342' WHEN 10 THEN '#795548' ELSE '#607D8B' END
                WHERE "Color" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "Bills"
                SET "CategoryId" = (
                    SELECT c."Id" FROM "Categories" c
                    WHERE c."LedgerId" = "Bills"."LedgerId" AND c."Name" = trim(substr(trim("Bills"."Category"), 1, 50)))
                WHERE "Category" IS NOT NULL AND trim("Category") <> '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Bills_CategoryId",
                table: "Bills",
                column: "CategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_Categories_CategoryId",
                table: "Bills",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Bills");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bills_Categories_CategoryId",
                table: "Bills");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Bills_CategoryId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Bills");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Bills",
                type: "TEXT",
                nullable: true);
        }
    }
}
