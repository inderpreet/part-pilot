using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentSequences",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    NextValue = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSequences", x => new { x.Name, x.Year });
                    table.CheckConstraint("CK_DocumentSequences_NextValue", "\"NextValue\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false, collation: "NOCASE"),
                    SupplierId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ExpectedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedById = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrders_Status", "\"Status\" IN ('Draft', 'Submitted', 'PartiallyReceived', 'Received', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PurchaseOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityOrdered = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityReceived = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    UnitCostCents = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLines", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrderLines_QuantityOrdered", "\"QuantityOrdered\" > 0");
                    table.CheckConstraint("CK_PurchaseOrderLines_QuantityReceived", "\"QuantityReceived\" >= 0");
                    table.CheckConstraint("CK_PurchaseOrderLines_UnitCostCents", "\"UnitCostCents\" >= 0");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_ProductId",
                table: "PurchaseOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_PurchaseOrderId_ProductId",
                table: "PurchaseOrderLines",
                columns: new[] { "PurchaseOrderId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CreatedById",
                table: "PurchaseOrders",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_Number",
                table: "PurchaseOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_Status",
                table: "PurchaseOrders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_SupplierId_OrderDate",
                table: "PurchaseOrders",
                columns: new[] { "SupplierId", "OrderDate" });

            // The foreign key StockTransactions.PurchaseOrderLineId -> PurchaseOrderLines.Id. SQLite cannot add a foreign key
            // in place, so the table is rebuilt (copy, drop, rename). EF would generate that rebuild itself, but it runs it
            // after every other operation, which would drop the append-only triggers right after we restored them. So the
            // rebuild is written out here, triggers included. Keep it in step with StockTransactionConfiguration.
            RebuildStockTransactions(migrationBuilder, withPurchaseOrderLineForeignKey: true);
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Take the foreign key off first (a rebuild, see Up), then the tables it pointed at can go.
            RebuildStockTransactions(migrationBuilder, withPurchaseOrderLineForeignKey: false);
            migrationBuilder.DropTable(
                name: "DocumentSequences");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLines");

            migrationBuilder.DropTable(
                name: "PurchaseOrders");
        }

        private static void RebuildStockTransactions(MigrationBuilder migrationBuilder, bool withPurchaseOrderLineForeignKey)
        {
            const string columns =
                "\"Id\", \"CreatedAt\", \"ImportBatchId\", \"Note\", \"OccurredAt\", \"ProductId\", \"PurchaseOrderLineId\", \"QuantityAfter\", " +
                "\"QuantityDelta\", \"Reason\", \"Reference\", \"ReversesTransactionId\", \"Type\", \"UnitCostCents\", \"UserId\"";

            var lineForeignKey = withPurchaseOrderLineForeignKey
                ? "    CONSTRAINT \"FK_StockTransactions_PurchaseOrderLines_PurchaseOrderLineId\" FOREIGN KEY (\"PurchaseOrderLineId\") REFERENCES \"PurchaseOrderLines\" (\"Id\") ON DELETE RESTRICT,\n"
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
                lineForeignKey +
                "    CONSTRAINT \"FK_StockTransactions_StockTransactions_ReversesTransactionId\" FOREIGN KEY (\"ReversesTransactionId\") REFERENCES \"StockTransactions\" (\"Id\") ON DELETE RESTRICT\n" +
                ");");
            migrationBuilder.Sql($"INSERT INTO \"ef_temp_StockTransactions\" ({columns}) SELECT {columns} FROM \"StockTransactions\";");

            // Same dance EF emits: foreign keys off (a no-op inside a transaction, so outside one), swap the tables, back on.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
            migrationBuilder.Sql("DROP TABLE \"StockTransactions\";");
            migrationBuilder.Sql("ALTER TABLE \"ef_temp_StockTransactions\" RENAME TO \"StockTransactions\";");
            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);

            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_OccurredAt\" ON \"StockTransactions\" (\"OccurredAt\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_ProductId_Id\" ON \"StockTransactions\" (\"ProductId\", \"Id\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_PurchaseOrderLineId\" ON \"StockTransactions\" (\"PurchaseOrderLineId\");");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_StockTransactions_ReversesTransactionId\" ON \"StockTransactions\" (\"ReversesTransactionId\") " +
                "WHERE \"ReversesTransactionId\" IS NOT NULL;");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_Type\" ON \"StockTransactions\" (\"Type\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_StockTransactions_UserId\" ON \"StockTransactions\" (\"UserId\");");

            // The append-only guards died with the old table (same statements as AddStockLedger).
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_StockTransactions_NoUpdate\" BEFORE UPDATE ON \"StockTransactions\" " +
                "BEGIN SELECT RAISE(ABORT, 'Stock transactions are immutable - post a Reversal instead.'); END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_StockTransactions_NoDelete\" BEFORE DELETE ON \"StockTransactions\" " +
                "BEGIN SELECT RAISE(ABORT, 'Stock transactions are immutable - post a Reversal instead.'); END;");
        }
    }
}