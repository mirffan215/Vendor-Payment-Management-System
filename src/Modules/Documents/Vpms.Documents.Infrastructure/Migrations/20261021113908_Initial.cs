using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Documents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.CreateTable(
                name: "Attachment",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ScanStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScanAttempts = table.Column<int>(type: "int", nullable: false),
                    ScanSignature = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ScannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploaderKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachment", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachment_ScanStatus",
                schema: "documents",
                table: "Attachment",
                column: "ScanStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Attachment_TenantId",
                schema: "documents",
                table: "Attachment",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachment_TenantId_OwnerType_OwnerId",
                schema: "documents",
                table: "Attachment",
                columns: new[] { "TenantId", "OwnerType", "OwnerId" });
        
            migrationBuilder.EnableTenantRowLevelSecurity("documents", "Attachment");
            migrationBuilder.GrantAppRoleOnSchema("documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("documents", "Attachment");

            migrationBuilder.DropTable(
                name: "Attachment",
                schema: "documents");
        }
    }
}
