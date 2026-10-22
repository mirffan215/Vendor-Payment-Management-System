using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VerificationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VerificationRuleSet",
                schema: "invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LoosenedRules = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationRuleSet", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuleSet_TenantId",
                schema: "invoice",
                table: "VerificationRuleSet",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuleSet_TenantId_LegalEntityId_VersionNo",
                schema: "invoice",
                table: "VerificationRuleSet",
                columns: new[] { "TenantId", "LegalEntityId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RuleSet_Pending",
                schema: "invoice",
                table: "VerificationRuleSet",
                column: "LegalEntityId",
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.EnableTenantRowLevelSecurity("invoice", "VerificationRuleSet");
            // Rule versions are history: superseded, rejected or cancelled, never deleted.
            migrationBuilder.Sql($"DENY DELETE ON [invoice].[VerificationRuleSet] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerificationRuleSet",
                schema: "invoice");
        }
    }
}
