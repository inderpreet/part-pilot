using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    RowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Options = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedById = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                    table.CheckConstraint("CK_ImportBatches_Kind", "\"Kind\" IN ('Products', 'Suppliers', 'ProductSuppliers', 'OpeningStock', 'Boms')");
                    table.CheckConstraint("CK_ImportBatches_Counts", "\"RowCount\" >= 0 AND \"CreatedCount\" >= 0 AND \"UpdatedCount\" >= 0 AND \"SkippedCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_ImportBatches_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_CreatedAt",
                table: "ImportBatches",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_CreatedById",
                table: "ImportBatches",
                column: "CreatedById");

            // SQLite cannot add a foreign key in place. Rebuild so ImportBatchId Restricts and the append-only
            // triggers survive (same approach as AddPurchaseOrders).
            RebuildStockTransactions(migrationBuilder, withImportBatchForeignKey: true);

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Key", "Value" },
                values: new object[,]
                {
                    { "Permission.ImportData.Staff", "false" },
                    { "Permission.ImportData.Viewer", "false" },
                    { "Permission.ImportBoms.Staff", "false" },
                    { "Permission.ImportBoms.Viewer", "false" },
                    { "Permission.ExportData.Staff", "true" },
                    { "Permission.ExportData.Viewer", "false" },
                    { "Permission.ExportBoms.Staff", "true" },
                    { "Permission.ExportBoms.Viewer", "false" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RebuildStockTransactions(migrationBuilder, withImportBatchForeignKey: false);

            migrationBuilder.DropTable(
                name: "ImportBatches");

            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ImportData.Staff");
            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ImportData.Viewer");
            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ImportBoms.Staff");
            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ImportBoms.Viewer");
            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ExportData.Staff");
            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ExportData.Viewer");
            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ExportBoms.Staff");
            migrationBuilder.DeleteData(table: "AppSettings", keyColumn: "Key", keyValue: "Permission.ExportBoms.Viewer");
        }

        private static void RebuildStockTransactions(MigrationBuilder migrationBuilder, bool withImportBatchForeignKey)
        {
            const string columns =
                "\"Id\", \"CreatedAt\", \"ImportBatchId\", \"Note\", \"OccurredAt\", \"ProductId\", \"PurchaseOrderLineId\", \"QuantityAfter\", " +
                "\"QuantityDelta\", \"Reason\", \"Reference\", \"ReversesTransactionId\", \"Type\", \"UnitCostCents\", \"UserId\"";

            var importBatchForeignKey = withImportBatchForeignKey
                ? "    CONSTRAINT \"FK_StockTransactions_ImportBatches_ImportBatchId\" FOREIGN KEY (\"ImportBatchId\") REFERENCES \"ImportBatches\" (\"Id\") ON DELETE RESTRICT,\n"
                : string.Empty;

            migrationBuilder.Sql(
                "CREATE TABLE \"ef_temp_StockTransactions\" (\n" +
                "    \"Id\" INTEGER NOT NULL CONSTRAINT \"PK_StockTransactions\" PRIMARY KEY AUTOINCREMENT,\n" +
                "    \"CreatedAt\" TEXT NOT NULL,\n" +
                "    \"ImportBatchId\" INTEGER NULL,\n" +
                "    \"Note\" TEXT NULL,\n" +
                "    \"OccurredAt\" TEXT NOT NULL,\n" +
                "    \"ProductId\" INTEGER NOT NULL,\n" +
                "    \"PurchaseOrderLineId\" INTEGER NULL,\n" +
                "    \"QuantityAfter\" INTEGER NOT NULL,\n" +
                "    \"QuantityDelta\" INTEGER NOT NULL,\n" +
                "    \"Reason\" TEXT NULL,\n" +
                "    \"Reference\" TEXT NULL,\n" +
                "    \"ReversesTransactionId\" INTEGER NULL,\n" +
                "    \"Type\" TEXT NOT NULL,\n" +
                "    \"UnitCostCents\" INTEGER NULL,\n" +
                "    \"UserId\" TEXT NOT NULL,\n" +
                "    CONSTRAINT \"CK_StockTransactions_QuantityDelta\" CHECK (\"QuantityDelta\" <> 0),\n" +
                "    CONSTRAINT \"CK_StockTransactions_Reversal\" CHECK ((\"Type\" = 'Reversal') = (\"ReversesTransactionId\" IS NOT NULL)),\n" +
                "    CONSTRAINT \"CK_StockTransactions_Type\" CHECK (\"Type\" IN ('Receive', 'Issue', 'Adjust', 'Count', 'Return', 'Reversal')),\n" +
                "    CONSTRAINT \"FK_StockTransactions_AspNetUsers_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"AspNetUsers\" (\"Id\") ON DELETE RESTRICT,\n" +
                "    CONSTRAINT \"FK_StockTransactions_Products_ProductId\" FOREIGN KEY (\"ProductId\") REFERENCES \"Products\" (\"Id\") ON DELETE RESTRICT,\n" +
                "    CONSTRAINT \"FK_StockTransactions_PurchaseOrderLines_PurchaseOrderLineId\" FOREIGN KEY (\"PurchaseOrderLineId\") REFERENCES \"PurchaseOrderLines\" (\"Id\") ON DELETE RESTRICT,\n" +
                importBatchForeignKey +
                "    CONSTRAINT \"FK_StockTransactions_StockTransactions_ReversesTransactionId\" FOREIGN KEY (\"ReversesTransactionId\") REFERENCES \"StockTransactions\" (\"Id\") ON DELETE RESTRICT\n" +
                ");");
            migrationBuilder.Sql($"INSERT INTO \"ef_temp_StockTransactions\" ({columns}) SELECT {columns} FROM \"StockTransactions\";");

            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
            migrationBuilder.Sql("DROP TABLE \"StockTransactions\";");
            migrationBuilder.Sql("ALTER TABLE \"ef_temp_StockTransactions\" RENAME TO \"StockTransactions\";");
            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);

            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_OccurredAt\" ON \"StockTransactions\" (\"OccurredAt\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_ProductId_Id\" ON \"StockTransactions\" (\"ProductId\", \"Id\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_ProductId_OccurredAt\" ON \"StockTransactions\" (\"ProductId\", \"OccurredAt\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_PurchaseOrderLineId\" ON \"StockTransactions\" (\"PurchaseOrderLineId\");");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_StockTransactions_ReversesTransactionId\" ON \"StockTransactions\" (\"ReversesTransactionId\") " +
                "WHERE \"ReversesTransactionId\" IS NOT NULL;");
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_StockTransactions_Type_OccurredAt_ProductId_QuantityDelta\" ON \"StockTransactions\" " +
                "(\"Type\", \"OccurredAt\", \"ProductId\", \"QuantityDelta\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_UserId\" ON \"StockTransactions\" (\"UserId\");");
            if (withImportBatchForeignKey)
            {
                migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_ImportBatchId\" ON \"StockTransactions\" (\"ImportBatchId\");");
            }

            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_StockTransactions_NoUpdate\" BEFORE UPDATE ON \"StockTransactions\" " +
                "BEGIN SELECT RAISE(ABORT, 'Stock transactions are immutable - post a Reversal instead.'); END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_StockTransactions_NoDelete\" BEFORE DELETE ON \"StockTransactions\" " +
                "BEGIN SELECT RAISE(ABORT, 'Stock transactions are immutable - post a Reversal instead.'); END;");
        }
    }
}
