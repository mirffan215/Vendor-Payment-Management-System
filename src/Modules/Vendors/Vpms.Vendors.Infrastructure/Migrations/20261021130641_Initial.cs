using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Vendors.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "vendor");

            migrationBuilder.CreateTable(
                name: "Vendor",
                schema: "vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    LegalNameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TradeName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CountryCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: false),
                    TaxIdCipher = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TaxIdIndex = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    RegistrationNoCipher = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VerificationStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastEditedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastEditedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SubmittedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubmittedByKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SuspendedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorDocument",
                schema: "vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VerifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploaderKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorDocument", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorEntityLink",
                schema: "vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ErpVendorAccount = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PaymentTermsCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    DefaultCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    DefaultTaxCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    DefaultDimensionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WithholdingRuleCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorEntityLink", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorMessage",
                schema: "vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorRequest",
                schema: "vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorRequest", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vendor_TenantId",
                schema: "vendor",
                table: "Vendor",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Vendor_TenantId_Status",
                schema: "vendor",
                table: "Vendor",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Vendor_TenantId_TaxIdIndex",
                schema: "vendor",
                table: "Vendor",
                columns: new[] { "TenantId", "TaxIdIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorDocument_AttachmentId",
                schema: "vendor",
                table: "VendorDocument",
                column: "AttachmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorDocument_TenantId",
                schema: "vendor",
                table: "VendorDocument",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDocument_TenantId_VendorId",
                schema: "vendor",
                table: "VendorDocument",
                columns: new[] { "TenantId", "VendorId" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorEntityLink_TenantId",
                schema: "vendor",
                table: "VendorEntityLink",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorEntityLink_VendorId_LegalEntityId",
                schema: "vendor",
                table: "VendorEntityLink",
                columns: new[] { "VendorId", "LegalEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorMessage_TenantId",
                schema: "vendor",
                table: "VendorMessage",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorMessage_TenantId_RequestId",
                schema: "vendor",
                table: "VendorMessage",
                columns: new[] { "TenantId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorRequest_TenantId",
                schema: "vendor",
                table: "VendorRequest",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRequest_TenantId_VendorId",
                schema: "vendor",
                table: "VendorRequest",
                columns: new[] { "TenantId", "VendorId" });
        
            migrationBuilder.EnableTenantRowLevelSecurity("vendor", "Vendor");
            migrationBuilder.EnableTenantRowLevelSecurity("vendor", "VendorDocument");
            migrationBuilder.EnableTenantRowLevelSecurity("vendor", "VendorEntityLink");
            migrationBuilder.EnableTenantRowLevelSecurity("vendor", "VendorRequest");
            migrationBuilder.EnableTenantRowLevelSecurity("vendor", "VendorMessage");
            migrationBuilder.GrantAppRoleOnSchema("vendor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("vendor", "VendorMessage");
            migrationBuilder.DisableTenantRowLevelSecurity("vendor", "VendorRequest");
            migrationBuilder.DisableTenantRowLevelSecurity("vendor", "VendorEntityLink");
            migrationBuilder.DisableTenantRowLevelSecurity("vendor", "VendorDocument");
            migrationBuilder.DisableTenantRowLevelSecurity("vendor", "Vendor");

            migrationBuilder.DropTable(
                name: "Vendor",
                schema: "vendor");

            migrationBuilder.DropTable(
                name: "VendorDocument",
                schema: "vendor");

            migrationBuilder.DropTable(
                name: "VendorEntityLink",
                schema: "vendor");

            migrationBuilder.DropTable(
                name: "VendorMessage",
                schema: "vendor");

            migrationBuilder.DropTable(
                name: "VendorRequest",
                schema: "vendor");
        }
    }
}
