using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_Type",
                table: "StockTransactions");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_ProductId_OccurredAt",
                table: "StockTransactions",
                columns: new[] { "ProductId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_Type_OccurredAt_ProductId_QuantityDelta",
                table: "StockTransactions",
                columns: new[] { "Type", "OccurredAt", "ProductId", "QuantityDelta" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_ProductId_OccurredAt",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_Type_OccurredAt_ProductId_QuantityDelta",
                table: "StockTransactions");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_Type",
                table: "StockTransactions",
                column: "Type");
        }
    }
}
