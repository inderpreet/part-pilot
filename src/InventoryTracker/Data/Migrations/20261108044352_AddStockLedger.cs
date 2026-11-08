using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockTransactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    QuantityDelta = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitCostCents = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PurchaseOrderLineId = table.Column<long>(type: "INTEGER", nullable: true),
                    ImportBatchId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReversesTransactionId = table.Column<long>(type: "INTEGER", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransactions", x => x.Id);
                    table.CheckConstraint("CK_StockTransactions_QuantityDelta", "\"QuantityDelta\" <> 0");
                    table.CheckConstraint("CK_StockTransactions_Reversal", "(\"Type\" = 'Reversal') = (\"ReversesTransactionId\" IS NOT NULL)");
                    table.CheckConstraint("CK_StockTransactions_Type", "\"Type\" IN ('Receive', 'Issue', 'Adjust', 'Count', 'Return', 'Reversal')");
                    table.ForeignKey(
                        name: "FK_StockTransactions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransactions_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransactions_StockTransactions_ReversesTransactionId",
                        column: x => x.ReversesTransactionId,
                        principalTable: "StockTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_OccurredAt",
                table: "StockTransactions",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_ProductId_Id",
                table: "StockTransactions",
                columns: new[] { "ProductId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_PurchaseOrderLineId",
                table: "StockTransactions",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_ReversesTransactionId",
                table: "StockTransactions",
                column: "ReversesTransactionId",
                unique: true,
                filter: "\"ReversesTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_Type",
                table: "StockTransactions",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_UserId",
                table: "StockTransactions",
                column: "UserId");

            // The ledger is append-only (architecture invariant 2, decision D14). A trigger makes that true even for
            // code that bypasses StockService, a SQL console or a buggy future feature. Hand-written: EF does not model triggers.
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_StockTransactions_NoUpdate\" BEFORE UPDATE ON \"StockTransactions\" " +
                "BEGIN SELECT RAISE(ABORT, 'Stock transactions are immutable - post a Reversal instead.'); END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_StockTransactions_NoDelete\" BEFORE DELETE ON \"StockTransactions\" " +
                "BEGIN SELECT RAISE(ABORT, 'Stock transactions are immutable - post a Reversal instead.'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_StockTransactions_NoUpdate\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_StockTransactions_NoDelete\";");

            migrationBuilder.DropTable(
                name: "StockTransactions");
        }
    }
}
