using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRequisitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Requisitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false, collation: "NOCASE"),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RequestedById = table.Column<string>(type: "TEXT", nullable: false),
                    NeededBy = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmittedById = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requisitions", x => x.Id);
                    table.CheckConstraint("CK_Requisitions_Status", "\"Status\" IN ('Draft', 'Submitted', 'Approved', 'Rejected', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_Requisitions_AspNetUsers_RequestedById",
                        column: x => x.RequestedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Requisitions_AspNetUsers_SubmittedById",
                        column: x => x.SubmittedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequisitionLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequisitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityRequested = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionLines", x => x.Id);
                    table.CheckConstraint("CK_RequisitionLines_QuantityRequested", "\"QuantityRequested\" >= 1");
                    table.ForeignKey(
                        name: "FK_RequisitionLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionLines_Requisitions_RequisitionId",
                        column: x => x.RequisitionId,
                        principalTable: "Requisitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Key", "Value" },
                values: new object[] { "Requisition.NumberPrefix", "REQ-" });

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLines_ProductId",
                table: "RequisitionLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLines_RequisitionId_ProductId",
                table: "RequisitionLines",
                columns: new[] { "RequisitionId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requisitions_Number",
                table: "Requisitions",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requisitions_RequestedById",
                table: "Requisitions",
                column: "RequestedById");

            migrationBuilder.CreateIndex(
                name: "IX_Requisitions_Status_SubmittedAt",
                table: "Requisitions",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Requisitions_SubmittedById",
                table: "Requisitions",
                column: "SubmittedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequisitionLines");

            migrationBuilder.DropTable(
                name: "Requisitions");

            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Key",
                keyValue: "Requisition.NumberPrefix");
        }
    }
}
