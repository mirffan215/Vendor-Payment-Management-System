using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Verification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceException",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InvoiceVersion = table.Column<int>(type: "int", nullable: false),
                    AssignedTo = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceException", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrder",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PreviouslyInvoicedValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ImportedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VerificationResult",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceVersion = table.Column<int>(type: "int", nullable: false),
                    RuleSetVersion = table.Column<int>(type: "int", nullable: false),
                    RuleCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationResult", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLine",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OrderedQuantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    PreviouslyInvoicedQuantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLine_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalSchema: "invoice",
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceException_TenantId",
                schema: "invoice",
                table: "InvoiceException",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceException_TenantId_LegalEntityId_Status",
                schema: "invoice",
                table: "InvoiceException",
                columns: new[] { "TenantId", "LegalEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_InvoiceException_Open",
                schema: "invoice",
                table: "InvoiceException",
                columns: new[] { "InvoiceId", "RuleCode" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_TenantId",
                schema: "invoice",
                table: "PurchaseOrder",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_TenantId_LegalEntityId_PoNumber",
                schema: "invoice",
                table: "PurchaseOrder",
                columns: new[] { "TenantId", "LegalEntityId", "PoNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_PurchaseOrderId_LineNo",
                schema: "invoice",
                table: "PurchaseOrderLine",
                columns: new[] { "PurchaseOrderId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_TenantId",
                schema: "invoice",
                table: "PurchaseOrderLine",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResult_TenantId",
                schema: "invoice",
                table: "VerificationResult",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResult_TenantId_InvoiceId_RunId",
                schema: "invoice",
                table: "VerificationResult",
                columns: new[] { "TenantId", "InvoiceId", "RunId" });

            foreach (var table in new[] { "VerificationResult", "InvoiceException", "PurchaseOrder", "PurchaseOrderLine" })
            {
                migrationBuilder.EnableTenantRowLevelSecurity("invoice", table);
            }

            // The record of what was checked and decided is never deleted.
            migrationBuilder.Sql($"DENY DELETE ON [invoice].[VerificationResult] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY DELETE ON [invoice].[InvoiceException] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY UPDATE ON [invoice].[VerificationResult] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceException",
                schema: "invoice");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLine",
                schema: "invoice");

            migrationBuilder.DropTable(
                name: "VerificationResult",
                schema: "invoice");

            migrationBuilder.DropTable(
                name: "PurchaseOrder",
                schema: "invoice");
        }
    }
}
