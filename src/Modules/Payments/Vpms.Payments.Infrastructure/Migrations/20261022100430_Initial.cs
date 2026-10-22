using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "payment");

            migrationBuilder.CreateTable(
                name: "PaymentInstruction",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Format = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CsvCipher = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    JsonCipher = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Signature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreparedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HandedOffBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HandedOffAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentInstruction", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProposal",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNo = table.Column<int>(type: "int", nullable: false),
                    ProposalNo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmittedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovalComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReleasedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProposal", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRequest",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InvoiceVersion = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    InvoiceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BankDetailsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankFingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InvolvedList = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseAuthorization",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorizedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DenialReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecheckJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SodJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InstructionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseAuthorization", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProposalItem",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProposalItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentProposalItem_PaymentProposal_ProposalId",
                        column: x => x.ProposalId,
                        principalSchema: "payment",
                        principalTable: "PaymentProposal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInstruction_ProposalId",
                schema: "payment",
                table: "PaymentInstruction",
                column: "ProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInstruction_TenantId",
                schema: "payment",
                table: "PaymentInstruction",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProposal_TenantId",
                schema: "payment",
                table: "PaymentProposal",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProposal_TenantId_LegalEntityId_SequenceNo",
                schema: "payment",
                table: "PaymentProposal",
                columns: new[] { "TenantId", "LegalEntityId", "SequenceNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProposal_TenantId_LegalEntityId_Status",
                schema: "payment",
                table: "PaymentProposal",
                columns: new[] { "TenantId", "LegalEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProposalItem_PaymentRequestId",
                schema: "payment",
                table: "PaymentProposalItem",
                column: "PaymentRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProposalItem_ProposalId_PaymentRequestId",
                schema: "payment",
                table: "PaymentProposalItem",
                columns: new[] { "ProposalId", "PaymentRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProposalItem_TenantId",
                schema: "payment",
                table: "PaymentProposalItem",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequest_TenantId",
                schema: "payment",
                table: "PaymentRequest",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequest_TenantId_LegalEntityId_Status_DueDate",
                schema: "payment",
                table: "PaymentRequest",
                columns: new[] { "TenantId", "LegalEntityId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequest_TenantId_ProposalId",
                schema: "payment",
                table: "PaymentRequest",
                columns: new[] { "TenantId", "ProposalId" });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentRequest_OpenInvoice",
                schema: "payment",
                table: "PaymentRequest",
                column: "InvoiceId",
                unique: true,
                filter: "[Status] IN ('Created', 'InProposal', 'Released')");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseAuthorization_TenantId",
                schema: "payment",
                table: "ReleaseAuthorization",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseAuthorization_TenantId_ProposalId",
                schema: "payment",
                table: "ReleaseAuthorization",
                columns: new[] { "TenantId", "ProposalId" });

            var tables = new[] { "PaymentRequest", "PaymentProposal", "PaymentProposalItem", "ReleaseAuthorization", "PaymentInstruction" };
            foreach (var table in tables)
            {
                migrationBuilder.EnableTenantRowLevelSecurity("payment", table);
            }

            migrationBuilder.GrantAppRoleOnSchema("payment");

            // Payment history is never deleted. Only the items of a draft proposal can be taken out (audited); release authorisations are append-only.
            foreach (var table in new[] { "PaymentRequest", "PaymentProposal", "ReleaseAuthorization", "PaymentInstruction" })
            {
                migrationBuilder.Sql($"DENY DELETE ON [payment].[{table}] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            }

            migrationBuilder.Sql($"DENY UPDATE ON [payment].[ReleaseAuthorization] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "PaymentRequest", "PaymentProposal", "PaymentProposalItem", "ReleaseAuthorization", "PaymentInstruction" })
            {
                migrationBuilder.DisableTenantRowLevelSecurity("payment", table);
            }

            migrationBuilder.DropTable(
                name: "PaymentInstruction",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "PaymentProposalItem",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "PaymentRequest",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "ReleaseAuthorization",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "PaymentProposal",
                schema: "payment");
        }
    }
}
