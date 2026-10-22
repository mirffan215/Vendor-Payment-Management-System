using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DelegationsAndDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscalatedAt",
                schema: "wf",
                table: "ApprovalTask",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalatedToList",
                schema: "wf",
                table: "ApprovalTask",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReminderSentAt",
                schema: "wf",
                table: "ApprovalTask",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Delegation",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowNotice",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNotice", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Delegation_TenantId",
                schema: "wf",
                table: "Delegation",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegation_TenantId_DelegateId_Status",
                schema: "wf",
                table: "Delegation",
                columns: new[] { "TenantId", "DelegateId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegation_TenantId_DelegatorId_Status",
                schema: "wf",
                table: "Delegation",
                columns: new[] { "TenantId", "DelegatorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegation_TenantId_LegalEntityId",
                schema: "wf",
                table: "Delegation",
                columns: new[] { "TenantId", "LegalEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotice_TaskId_UserId_Kind",
                schema: "wf",
                table: "WorkflowNotice",
                columns: new[] { "TaskId", "UserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotice_TenantId",
                schema: "wf",
                table: "WorkflowNotice",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotice_TenantId_UserId_ReadAt",
                schema: "wf",
                table: "WorkflowNotice",
                columns: new[] { "TenantId", "UserId", "ReadAt" });

            foreach (var table in new[] { "Delegation", "WorkflowNotice" })
            {
                migrationBuilder.EnableTenantRowLevelSecurity("wf", table);
                migrationBuilder.Sql($"DENY DELETE ON [wf].[{table}] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("wf", "Delegation");
            migrationBuilder.DisableTenantRowLevelSecurity("wf", "WorkflowNotice");

            migrationBuilder.DropTable(
                name: "Delegation",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "WorkflowNotice",
                schema: "wf");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                schema: "wf",
                table: "ApprovalTask");

            migrationBuilder.DropColumn(
                name: "EscalatedToList",
                schema: "wf",
                table: "ApprovalTask");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                schema: "wf",
                table: "ApprovalTask");
        }
    }
}
