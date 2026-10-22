using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.CustomerAdmin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Retention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegalHold",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PlacedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlacedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleaseComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHold", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionPolicyVersion",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                    table.PrimaryKey("PK_RetentionPolicyVersion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegalHold_TenantId",
                schema: "customer",
                table: "LegalHold",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalHold_TenantId_ReleasedAt",
                schema: "customer",
                table: "LegalHold",
                columns: new[] { "TenantId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionPolicyVersion_TenantId_VersionNo",
                schema: "customer",
                table: "RetentionPolicyVersion",
                columns: new[] { "TenantId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RetentionPolicy_Pending",
                schema: "customer",
                table: "RetentionPolicyVersion",
                column: "TenantId",
                unique: true,
                filter: "[Status] = 'Pending'");

            foreach (var table in new[] { "RetentionPolicyVersion", "LegalHold" })
            {
                migrationBuilder.EnableTenantRowLevelSecurity("customer", table);
                // Policy versions and holds are evidence of what was decided and by whom: they are never deleted.
                migrationBuilder.Sql($"DENY DELETE ON [customer].[{table}] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "RetentionPolicyVersion", "LegalHold" })
            {
                migrationBuilder.DisableTenantRowLevelSecurity("customer", table);
            }

            migrationBuilder.DropTable(
                name: "LegalHold",
                schema: "customer");

            migrationBuilder.DropTable(
                name: "RetentionPolicyVersion",
                schema: "customer");
        }
    }
}
