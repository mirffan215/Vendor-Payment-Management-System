using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.VendorPortal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "portal");

            migrationBuilder.CreateTable(
                name: "VendorInvite",
                schema: "portal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IssuedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorInvite", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorRecoveryCode",
                schema: "portal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorRecoveryCode", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorUser",
                schema: "portal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TotpSecretCipher = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TotpEnrolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTotpStep = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MfaResetRequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MfaResetRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorUser", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorInvite_TenantId",
                schema: "portal",
                table: "VendorInvite",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorInvite_TenantId_VendorUserId",
                schema: "portal",
                table: "VendorInvite",
                columns: new[] { "TenantId", "VendorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorInvite_TokenHash",
                schema: "portal",
                table: "VendorInvite",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorRecoveryCode_TenantId",
                schema: "portal",
                table: "VendorRecoveryCode",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRecoveryCode_VendorUserId_CodeHash",
                schema: "portal",
                table: "VendorRecoveryCode",
                columns: new[] { "VendorUserId", "CodeHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorUser_TenantId",
                schema: "portal",
                table: "VendorUser",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorUser_TenantId_Email",
                schema: "portal",
                table: "VendorUser",
                columns: new[] { "TenantId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorUser_TenantId_VendorId",
                schema: "portal",
                table: "VendorUser",
                columns: new[] { "TenantId", "VendorId" });
        
            migrationBuilder.EnableTenantRowLevelSecurity("portal", "VendorUser");
            migrationBuilder.EnableTenantRowLevelSecurity("portal", "VendorInvite");
            migrationBuilder.EnableTenantRowLevelSecurity("portal", "VendorRecoveryCode");
            migrationBuilder.GrantAppRoleOnSchema("portal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("portal", "VendorRecoveryCode");
            migrationBuilder.DisableTenantRowLevelSecurity("portal", "VendorInvite");
            migrationBuilder.DisableTenantRowLevelSecurity("portal", "VendorUser");

            migrationBuilder.DropTable(
                name: "VendorInvite",
                schema: "portal");

            migrationBuilder.DropTable(
                name: "VendorRecoveryCode",
                schema: "portal");

            migrationBuilder.DropTable(
                name: "VendorUser",
                schema: "portal");
        }
    }
}
