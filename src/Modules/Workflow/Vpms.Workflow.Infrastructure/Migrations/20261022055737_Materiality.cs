using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Materiality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaterialityPolicy",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                    table.PrimaryKey("PK_MaterialityPolicy", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialityPolicy_TenantId",
                schema: "wf",
                table: "MaterialityPolicy",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialityPolicy_TenantId_LegalEntityId_SubjectType_VersionNo",
                schema: "wf",
                table: "MaterialityPolicy",
                columns: new[] { "TenantId", "LegalEntityId", "SubjectType", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_MaterialityPolicy_Pending",
                schema: "wf",
                table: "MaterialityPolicy",
                columns: new[] { "LegalEntityId", "SubjectType" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.EnableTenantRowLevelSecurity("wf", "MaterialityPolicy");
            migrationBuilder.Sql($"DENY DELETE ON [wf].[MaterialityPolicy] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("wf", "MaterialityPolicy");

            migrationBuilder.DropTable(
                name: "MaterialityPolicy",
                schema: "wf");
        }
    }
}
