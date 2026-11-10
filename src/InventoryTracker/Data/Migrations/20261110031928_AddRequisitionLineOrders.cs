using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRequisitionLineOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequisitionLineOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequisitionLineId = table.Column<long>(type: "INTEGER", nullable: false),
                    PurchaseOrderLineId = table.Column<long>(type: "INTEGER", nullable: false),
                    QuantityOrdered = table.Column<int>(type: "INTEGER", nullable: false),
                    ConvertedById = table.Column<string>(type: "TEXT", nullable: false),
                    ConvertedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionLineOrders", x => x.Id);
                    table.CheckConstraint("CK_RequisitionLineOrders_QuantityOrdered", "\"QuantityOrdered\" >= 1");
                    table.ForeignKey(
                        name: "FK_RequisitionLineOrders_AspNetUsers_ConvertedById",
                        column: x => x.ConvertedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionLineOrders_PurchaseOrderLines_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RequisitionLineOrders_RequisitionLines_RequisitionLineId",
                        column: x => x.RequisitionLineId,
                        principalTable: "RequisitionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLineOrders_ConvertedById",
                table: "RequisitionLineOrders",
                column: "ConvertedById");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLineOrders_PurchaseOrderLineId",
                table: "RequisitionLineOrders",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLineOrders_RequisitionLineId",
                table: "RequisitionLineOrders",
                column: "RequisitionLineId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequisitionLineOrders");
        }
    }
}
