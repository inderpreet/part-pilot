using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace InventoryTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRequisitionApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequisitionCancellations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequisitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CancelledById = table.Column<string>(type: "TEXT", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionCancellations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequisitionCancellations_AspNetUsers_CancelledById",
                        column: x => x.CancelledById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionCancellations_Requisitions_RequisitionId",
                        column: x => x.RequisitionId,
                        principalTable: "Requisitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequisitionLineDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequisitionLineId = table.Column<long>(type: "INTEGER", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    QuantityApproved = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DecidedById = table.Column<string>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionLineDecisions", x => x.Id);
                    table.CheckConstraint("CK_RequisitionLineDecisions_Decision", "\"Decision\" IN ('Approved', 'Rejected')");
                    table.CheckConstraint("CK_RequisitionLineDecisions_Quantity", "(\"Decision\" = 'Approved' AND \"QuantityApproved\" >= 1) OR (\"Decision\" = 'Rejected' AND \"QuantityApproved\" = 0)");
                    table.ForeignKey(
                        name: "FK_RequisitionLineDecisions_AspNetUsers_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionLineDecisions_RequisitionLines_RequisitionLineId",
                        column: x => x.RequisitionLineId,
                        principalTable: "RequisitionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Key", "Value" },
                values: new object[,]
                {
                    { "Permission.ExportBoms.Manager", "true" },
                    { "Permission.ExportData.Manager", "true" },
                    { "Permission.ImportBoms.Manager", "false" },
                    { "Permission.ImportData.Manager", "false" }
                });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[] { "a1b2c3d4-0004-4000-8000-000000000004", "a1b2c3d4-0004-4000-8000-000000000004", "Manager", "MANAGER" });

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionCancellations_CancelledById",
                table: "RequisitionCancellations",
                column: "CancelledById");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionCancellations_RequisitionId",
                table: "RequisitionCancellations",
                column: "RequisitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLineDecisions_DecidedById",
                table: "RequisitionLineDecisions",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLineDecisions_RequisitionLineId",
                table: "RequisitionLineDecisions",
                column: "RequisitionLineId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequisitionCancellations");

            migrationBuilder.DropTable(
                name: "RequisitionLineDecisions");

            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Key",
                keyValue: "Permission.ExportBoms.Manager");

            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Key",
                keyValue: "Permission.ExportData.Manager");

            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Key",
                keyValue: "Permission.ImportBoms.Manager");

            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Key",
                keyValue: "Permission.ImportData.Manager");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "a1b2c3d4-0004-4000-8000-000000000004");
        }
    }
}
