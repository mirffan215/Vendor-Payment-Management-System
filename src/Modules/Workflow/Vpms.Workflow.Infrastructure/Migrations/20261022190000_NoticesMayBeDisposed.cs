using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Workflow.Infrastructure.Migrations
{
    /// <summary>
    /// Reminder and escalation notices are informational copies: the escalation itself is recorded in the approval history and the audit trail. The
    /// retention job (ADR-0024) deletes old notices, so the application role may delete them again (the earlier DENY is lifted).
    /// </summary>
    [DbContext(typeof(WorkflowDbContext))]
    [Migration("20261022190000_NoticesMayBeDisposed")]
    public partial class NoticesMayBeDisposed : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"REVOKE DELETE ON [wf].[WorkflowNotice] FROM [{TenantSecurityMigrationExtensions.AppRole}];");

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"DENY DELETE ON [wf].[WorkflowNotice] TO [{TenantSecurityMigrationExtensions.AppRole}];");
    }
}
