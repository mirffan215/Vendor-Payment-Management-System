using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "invoice");

            migrationBuilder.CreateTable(
                name: "Invoice",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PoReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SupplierTaxInvoiceRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    DuplicateKeyIndex = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    NumberKeyIndex = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastEditedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastEditedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoice", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceAttachment",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AddedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddedByKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceAttachment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLine",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    LineAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    TaxRate = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PoLineRef = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DimensionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "invoice",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_TenantId",
                schema: "invoice",
                table: "Invoice",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_TenantId_DuplicateKeyIndex",
                schema: "invoice",
                table: "Invoice",
                columns: new[] { "TenantId", "DuplicateKeyIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_TenantId_LegalEntityId_Status",
                schema: "invoice",
                table: "Invoice",
                columns: new[] { "TenantId", "LegalEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_TenantId_VendorId_NumberKeyIndex",
                schema: "invoice",
                table: "Invoice",
                columns: new[] { "TenantId", "VendorId", "NumberKeyIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceAttachment_AttachmentId",
                schema: "invoice",
                table: "InvoiceAttachment",
                column: "AttachmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceAttachment_TenantId",
                schema: "invoice",
                table: "InvoiceAttachment",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceAttachment_TenantId_InvoiceId",
                schema: "invoice",
                table: "InvoiceAttachment",
                columns: new[] { "TenantId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_InvoiceId_LineNo",
                schema: "invoice",
                table: "InvoiceLine",
                columns: new[] { "InvoiceId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_TenantId",
                schema: "invoice",
                table: "InvoiceLine",
                column: "TenantId");

            migrationBuilder.EnableTenantRowLevelSecurity("invoice", "Invoice");
            migrationBuilder.EnableTenantRowLevelSecurity("invoice", "InvoiceLine");
            migrationBuilder.EnableTenantRowLevelSecurity("invoice", "InvoiceAttachment");
            migrationBuilder.GrantAppRoleOnSchema("invoice");
            // Invoices are financial records: they are cancelled or rejected, never deleted.
            migrationBuilder.Sql($"DENY DELETE ON [invoice].[Invoice] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY DELETE ON [invoice].[InvoiceAttachment] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceAttachment",
                schema: "invoice");

            migrationBuilder.DropTable(
                name: "InvoiceLine",
                schema: "invoice");

            migrationBuilder.DropTable(
                name: "Invoice",
                schema: "invoice");
        }
    }
}
