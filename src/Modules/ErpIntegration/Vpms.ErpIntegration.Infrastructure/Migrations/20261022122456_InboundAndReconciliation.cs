using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.ErpIntegration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InboundAndReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErpInboxMessage",
                schema: "erp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpInboxMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErpPullState",
                schema: "erp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastPulledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpPullState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationItem",
                schema: "erp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ItemKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErpReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VpmsValue = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ErpValue = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    FirstSeenRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastSeenRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProposedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProposedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationItem", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRun",
                schema: "erp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    VpmsInvoices = table.Column<int>(type: "int", nullable: false),
                    ErpDocuments = table.Column<int>(type: "int", nullable: false),
                    NewItems = table.Column<int>(type: "int", nullable: false),
                    OpenItems = table.Column<int>(type: "int", nullable: false),
                    ClearedItems = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRun", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErpInboxMessage_TenantId_LegalEntityId_Kind_ExternalId_Sequence",
                schema: "erp",
                table: "ErpInboxMessage",
                columns: new[] { "TenantId", "LegalEntityId", "Kind", "ExternalId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpInboxMessage_TenantId_MessageId",
                schema: "erp",
                table: "ErpInboxMessage",
                columns: new[] { "TenantId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpInboxMessage_TenantId_Status",
                schema: "erp",
                table: "ErpInboxMessage",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpPullState_TenantId_LegalEntityId_Kind",
                schema: "erp",
                table: "ErpPullState",
                columns: new[] { "TenantId", "LegalEntityId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationItem_TenantId_LegalEntityId_ItemKey",
                schema: "erp",
                table: "ReconciliationItem",
                columns: new[] { "TenantId", "LegalEntityId", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationItem_TenantId_LegalEntityId_Status",
                schema: "erp",
                table: "ReconciliationItem",
                columns: new[] { "TenantId", "LegalEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRun_TenantId_LegalEntityId_StartedAt",
                schema: "erp",
                table: "ReconciliationRun",
                columns: new[] { "TenantId", "LegalEntityId", "StartedAt" });

            foreach (var table in new[] { "ErpInboxMessage", "ErpPullState", "ReconciliationRun", "ReconciliationItem" })
            {
                migrationBuilder.EnableTenantRowLevelSecurity("erp", table);
            }

            migrationBuilder.GrantAppRoleOnSchema("erp");

            // What the ERP told us, the runs and the differences found are history: nothing is deleted, and a run is never edited.
            foreach (var table in new[] { "ErpInboxMessage", "ReconciliationRun", "ReconciliationItem" })
            {
                migrationBuilder.Sql($"DENY DELETE ON [erp].[{table}] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "ErpInboxMessage", "ErpPullState", "ReconciliationRun", "ReconciliationItem" })
            {
                migrationBuilder.DisableTenantRowLevelSecurity("erp", table);
            }

            migrationBuilder.DropTable(
                name: "ErpInboxMessage",
                schema: "erp");

            migrationBuilder.DropTable(
                name: "ErpPullState",
                schema: "erp");

            migrationBuilder.DropTable(
                name: "ReconciliationItem",
                schema: "erp");

            migrationBuilder.DropTable(
                name: "ReconciliationRun",
                schema: "erp");
        }
    }
}
