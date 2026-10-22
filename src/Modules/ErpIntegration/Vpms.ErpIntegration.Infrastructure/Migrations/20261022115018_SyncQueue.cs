using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.ErpIntegration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErpSyncAttempt",
                schema: "erp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNo = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpSyncAttempt", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErpSyncRecord",
                schema: "erp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ErpReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HandledBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HandlingNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SourceAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpSyncRecord", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErpSyncAttempt_TenantId_RecordId_AttemptNo",
                schema: "erp",
                table: "ErpSyncAttempt",
                columns: new[] { "TenantId", "RecordId", "AttemptNo" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpSyncRecord_TenantId_IdempotencyKey",
                schema: "erp",
                table: "ErpSyncRecord",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpSyncRecord_TenantId_LegalEntityId_Type_ObjectId",
                schema: "erp",
                table: "ErpSyncRecord",
                columns: new[] { "TenantId", "LegalEntityId", "Type", "ObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ErpSyncRecord_TenantId_Status_NextAttemptAt",
                schema: "erp",
                table: "ErpSyncRecord",
                columns: new[] { "TenantId", "Status", "NextAttemptAt" });

            foreach (var table in new[] { "ErpSyncRecord", "ErpSyncAttempt" })
            {
                migrationBuilder.EnableTenantRowLevelSecurity("erp", table);
            }

            migrationBuilder.GrantAppRoleOnSchema("erp");

            // Queue history is never deleted, and the record of what the ERP answered is append-only.
            migrationBuilder.Sql($"DENY DELETE ON [erp].[ErpSyncRecord] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY DELETE, UPDATE ON [erp].[ErpSyncAttempt] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("erp", "ErpSyncRecord");
            migrationBuilder.DisableTenantRowLevelSecurity("erp", "ErpSyncAttempt");

            migrationBuilder.DropTable(
                name: "ErpSyncAttempt",
                schema: "erp");

            migrationBuilder.DropTable(
                name: "ErpSyncRecord",
                schema: "erp");
        }
    }
}
