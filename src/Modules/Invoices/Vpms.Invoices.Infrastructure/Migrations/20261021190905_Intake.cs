using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Intake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntakeMessage",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalMessageId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttachmentIds = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceExtraction",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Engine = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceExtraction", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceMailbox",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceMailbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntakeMessage_TenantId",
                schema: "invoice",
                table: "IntakeMessage",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeMessage_TenantId_LegalEntityId_ExternalMessageId",
                schema: "invoice",
                table: "IntakeMessage",
                columns: new[] { "TenantId", "LegalEntityId", "ExternalMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntakeMessage_TenantId_LegalEntityId_Status",
                schema: "invoice",
                table: "IntakeMessage",
                columns: new[] { "TenantId", "LegalEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceExtraction_AttachmentId",
                schema: "invoice",
                table: "InvoiceExtraction",
                column: "AttachmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceExtraction_TenantId",
                schema: "invoice",
                table: "InvoiceExtraction",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceExtraction_TenantId_InvoiceId",
                schema: "invoice",
                table: "InvoiceExtraction",
                columns: new[] { "TenantId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMailbox_TenantId",
                schema: "invoice",
                table: "InvoiceMailbox",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMailbox_TenantId_Address",
                schema: "invoice",
                table: "InvoiceMailbox",
                columns: new[] { "TenantId", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMailbox_TenantId_LegalEntityId",
                schema: "invoice",
                table: "InvoiceMailbox",
                columns: new[] { "TenantId", "LegalEntityId" },
                unique: true);

            migrationBuilder.EnableTenantRowLevelSecurity("invoice", "InvoiceExtraction");
            migrationBuilder.EnableTenantRowLevelSecurity("invoice", "InvoiceMailbox");
            migrationBuilder.EnableTenantRowLevelSecurity("invoice", "IntakeMessage");
            migrationBuilder.Sql($"DENY DELETE ON [invoice].[IntakeMessage] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntakeMessage",
                schema: "invoice");

            migrationBuilder.DropTable(
                name: "InvoiceExtraction",
                schema: "invoice");

            migrationBuilder.DropTable(
                name: "InvoiceMailbox",
                schema: "invoice");
        }
    }
}
