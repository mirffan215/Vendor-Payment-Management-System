using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Vendors.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BankDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankDetailChange",
                schema: "vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposedBankDetailsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplacesBankDetailsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MadeBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MadeAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CheckedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CheckerComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CallbackVerificationRef = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankDetailChange", x => x.Id);
                    table.CheckConstraint("CK_BankDetailChange_DecisionHasChecker", "[Status] NOT IN ('Approved','Rejected') OR ([CheckedBy] IS NOT NULL AND [CheckedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_BankDetailChange_DifferentPerson", "[CheckedBy] IS NULL OR [CheckedBy] <> [MadeBy]");
                });

            migrationBuilder.CreateTable(
                name: "BankDetails",
                schema: "vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BankName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SwiftBic = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    CountryCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    IbanCipher = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IbanIndex = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    AccountNumberCipher = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AccountNumberIndex = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    MaskedAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankDetails", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankDetailChange_ProposedBankDetailsId",
                schema: "vendor",
                table: "BankDetailChange",
                column: "ProposedBankDetailsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankDetailChange_TenantId",
                schema: "vendor",
                table: "BankDetailChange",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BankDetailChange_TenantId_VendorId_Status",
                schema: "vendor",
                table: "BankDetailChange",
                columns: new[] { "TenantId", "VendorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BankDetails_TenantId",
                schema: "vendor",
                table: "BankDetails",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BankDetails_TenantId_IbanIndex",
                schema: "vendor",
                table: "BankDetails",
                columns: new[] { "TenantId", "IbanIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_BankDetails_TenantId_VendorId_Status",
                schema: "vendor",
                table: "BankDetails",
                columns: new[] { "TenantId", "VendorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_BankDetails_Verified",
                schema: "vendor",
                table: "BankDetails",
                columns: new[] { "VendorId", "CurrencyCode" },
                unique: true,
                filter: "[Status] = 'Verified'");
        
            migrationBuilder.EnableTenantRowLevelSecurity("vendor", "BankDetails");
            migrationBuilder.EnableTenantRowLevelSecurity("vendor", "BankDetailChange");

            // Two-person rule at the database (ADR-0008): bank details can only become Verified through a change that a
            // different person approved, and a decided change can never be altered. Evidence is never deleted.
            migrationBuilder.Sql("""
                CREATE TRIGGER [vendor].[trg_BankDetails_VerifiedNeedsApproval] ON [vendor].[BankDetails]
                AFTER INSERT, UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted i WHERE i.[Status] = 'Verified' AND NOT EXISTS (
                        SELECT 1 FROM [vendor].[BankDetailChange] c
                        WHERE c.[ProposedBankDetailsId] = i.[Id] AND c.[Status] = 'Approved'
                          AND c.[CheckedBy] IS NOT NULL AND c.[CheckedBy] <> c.[MadeBy]))
                    BEGIN
                        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                        THROW 51010, 'Bank details can only be verified through a change approved by a different person.', 1;
                    END
                END
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER [vendor].[trg_BankDetailChange_Terminal] ON [vendor].[BankDetailChange]
                AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted d WHERE d.[Status] IN ('Approved','Rejected','Cancelled','Expired'))
                    BEGIN
                        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                        THROW 51011, 'A decided bank-detail change cannot be modified.', 1;
                    END
                END
                """);
            migrationBuilder.Sql($"DENY DELETE ON [vendor].[BankDetails] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY DELETE ON [vendor].[BankDetailChange] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [vendor].[trg_BankDetailChange_Terminal];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [vendor].[trg_BankDetails_VerifiedNeedsApproval];");
            migrationBuilder.DisableTenantRowLevelSecurity("vendor", "BankDetailChange");
            migrationBuilder.DisableTenantRowLevelSecurity("vendor", "BankDetails");

            migrationBuilder.DropTable(
                name: "BankDetailChange",
                schema: "vendor");

            migrationBuilder.DropTable(
                name: "BankDetails",
                schema: "vendor");
        }
    }
}
