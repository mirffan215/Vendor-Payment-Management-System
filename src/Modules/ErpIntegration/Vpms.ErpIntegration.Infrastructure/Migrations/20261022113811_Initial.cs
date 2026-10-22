using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.ErpIntegration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "erp");

            migrationBuilder.CreateTable(
                name: "ErpConfig",
                schema: "erp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
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
                    table.PrimaryKey("PK_ErpConfig", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErpConfig_TenantId_Kind_LegalEntityId_VersionNo",
                schema: "erp",
                table: "ErpConfig",
                columns: new[] { "TenantId", "Kind", "LegalEntityId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ErpConfig_Pending",
                schema: "erp",
                table: "ErpConfig",
                columns: new[] { "TenantId", "Kind", "LegalEntityId" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.EnableTenantRowLevelSecurity("erp", "ErpConfig");
            migrationBuilder.GrantAppRoleOnSchema("erp");

            // Configuration history is never deleted: superseded versions show what was in force when a document was posted.
            migrationBuilder.Sql($"DENY DELETE ON [erp].[ErpConfig] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("erp", "ErpConfig");

            migrationBuilder.DropTable(
                name: "ErpConfig",
                schema: "erp");
        }
    }
}
