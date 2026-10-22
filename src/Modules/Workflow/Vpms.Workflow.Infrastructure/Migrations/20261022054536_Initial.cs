using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "wf");

            migrationBuilder.CreateTable(
                name: "WorkflowDefinition",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    SpecJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SpecHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDefinition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRun",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectVersion = table.Column<int>(type: "int", nullable: false),
                    SubjectLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "int", nullable: false),
                    DefinitionHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StartedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OutcomeAppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Makers = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FactsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalTask",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BranchPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AssigneeList = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Quorum = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReminderAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalTask", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalTask_WorkflowRun_RunId",
                        column: x => x.RunId,
                        principalSchema: "wf",
                        principalTable: "WorkflowRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JoinArrival",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Arrived = table.Column<int>(type: "int", nullable: false),
                    Fired = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JoinArrival", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JoinArrival_WorkflowRun_RunId",
                        column: x => x.RunId,
                        principalSchema: "wf",
                        principalTable: "WorkflowRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskDecision",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActingForId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDecision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskDecision_ApprovalTask_TaskId",
                        column: x => x.TaskId,
                        principalSchema: "wf",
                        principalTable: "ApprovalTask",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTask_RunId",
                schema: "wf",
                table: "ApprovalTask",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTask_TenantId",
                schema: "wf",
                table: "ApprovalTask",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTask_TenantId_RunId_Status",
                schema: "wf",
                table: "ApprovalTask",
                columns: new[] { "TenantId", "RunId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTask_TenantId_Status_DueAt",
                schema: "wf",
                table: "ApprovalTask",
                columns: new[] { "TenantId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JoinArrival_RunId_Key",
                schema: "wf",
                table: "JoinArrival",
                columns: new[] { "RunId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JoinArrival_TenantId",
                schema: "wf",
                table: "JoinArrival",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDecision_TaskId_ActorId",
                schema: "wf",
                table: "TaskDecision",
                columns: new[] { "TaskId", "ActorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDecision_TenantId",
                schema: "wf",
                table: "TaskDecision",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDecision_TenantId_ActorId",
                schema: "wf",
                table: "TaskDecision",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_TenantId",
                schema: "wf",
                table: "WorkflowDefinition",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinition_TenantId_LegalEntityId_SubjectType_VersionNo",
                schema: "wf",
                table: "WorkflowDefinition",
                columns: new[] { "TenantId", "LegalEntityId", "SubjectType", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowDefinition_Pending",
                schema: "wf",
                table: "WorkflowDefinition",
                columns: new[] { "LegalEntityId", "SubjectType" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRun_TenantId",
                schema: "wf",
                table: "WorkflowRun",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRun_TenantId_LegalEntityId_Status",
                schema: "wf",
                table: "WorkflowRun",
                columns: new[] { "TenantId", "LegalEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRun_TenantId_SubjectType_SubjectId_Status",
                schema: "wf",
                table: "WorkflowRun",
                columns: new[] { "TenantId", "SubjectType", "SubjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowRun_ActiveSubject",
                schema: "wf",
                table: "WorkflowRun",
                columns: new[] { "SubjectType", "SubjectId" },
                unique: true,
                filter: "[Status] = 'Active'");

            var tables = new[] { "WorkflowDefinition", "WorkflowRun", "ApprovalTask", "TaskDecision", "JoinArrival" };
            foreach (var table in tables)
            {
                migrationBuilder.EnableTenantRowLevelSecurity("wf", table);
            }

            migrationBuilder.GrantAppRoleOnSchema("wf");

            // Approval history is append-only: nothing is deleted; decisions are never changed.
            foreach (var table in tables)
            {
                migrationBuilder.Sql($"DENY DELETE ON [wf].[{table}] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            }

            migrationBuilder.Sql($"DENY UPDATE ON [wf].[TaskDecision] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "WorkflowDefinition", "WorkflowRun", "ApprovalTask", "TaskDecision", "JoinArrival" })
            {
                migrationBuilder.DisableTenantRowLevelSecurity("wf", table);
            }

            migrationBuilder.DropTable(
                name: "JoinArrival",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "TaskDecision",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "WorkflowDefinition",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "ApprovalTask",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "WorkflowRun",
                schema: "wf");
        }
    }
}
