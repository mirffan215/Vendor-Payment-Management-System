using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.CustomerAdmin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdminModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultCalendar",
                schema: "customer",
                table: "Tenant",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "gregorian");

            migrationBuilder.AddColumn<Guid>(
                name: "LogoAttachmentId",
                schema: "customer",
                table: "Tenant",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryColor",
                schema: "customer",
                table: "Tenant",
                type: "nvarchar(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalNameAr",
                schema: "customer",
                table: "LegalEntity",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegistrationNoCipher",
                schema: "customer",
                table: "LegalEntity",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegistrationNoIndex",
                schema: "customer",
                table: "LegalEntity",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovalAuthority",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    DimensionScopeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalAuthority", x => x.Id);
                    table.CheckConstraint("CK_ApprovalAuthority_Amount", "[MaxAmount] > 0");
                });

            migrationBuilder.CreateTable(
                name: "ApprovalMatrixRule",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ConditionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApproverResolutionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Levels = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalMatrixRule", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntity_TenantId_TaxRegistrationNoIndex",
                schema: "customer",
                table: "LegalEntity",
                columns: new[] { "TenantId", "TaxRegistrationNoIndex" },
                unique: true,
                filter: "[TaxRegistrationNoIndex] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAuthority_TenantId",
                schema: "customer",
                table: "ApprovalAuthority",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAuthority_TenantId_LegalEntityId_UserId",
                schema: "customer",
                table: "ApprovalAuthority",
                columns: new[] { "TenantId", "LegalEntityId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalMatrixRule_TenantId",
                schema: "customer",
                table: "ApprovalMatrixRule",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalMatrixRule_TenantId_LegalEntityId_Priority",
                schema: "customer",
                table: "ApprovalMatrixRule",
                columns: new[] { "TenantId", "LegalEntityId", "Priority" });
        
            migrationBuilder.EnableTenantRowLevelSecurity("customer", "ApprovalAuthority");
            migrationBuilder.EnableTenantRowLevelSecurity("customer", "ApprovalMatrixRule");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("customer", "ApprovalMatrixRule");
            migrationBuilder.DisableTenantRowLevelSecurity("customer", "ApprovalAuthority");

            migrationBuilder.DropTable(
                name: "ApprovalAuthority",
                schema: "customer");

            migrationBuilder.DropTable(
                name: "ApprovalMatrixRule",
                schema: "customer");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntity_TenantId_TaxRegistrationNoIndex",
                schema: "customer",
                table: "LegalEntity");

            migrationBuilder.DropColumn(
                name: "DefaultCalendar",
                schema: "customer",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "LogoAttachmentId",
                schema: "customer",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "PrimaryColor",
                schema: "customer",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "LegalNameAr",
                schema: "customer",
                table: "LegalEntity");

            migrationBuilder.DropColumn(
                name: "TaxRegistrationNoCipher",
                schema: "customer",
                table: "LegalEntity");

            migrationBuilder.DropColumn(
                name: "TaxRegistrationNoIndex",
                schema: "customer",
                table: "LegalEntity");
        }
    }
}
