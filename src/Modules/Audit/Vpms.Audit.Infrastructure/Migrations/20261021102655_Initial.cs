using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "Anchor",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    AnchoredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExternalRef = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anchor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvent",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OnBehalfOfId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    BeforeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AfterHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ChangeSummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PrevHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChainHead",
                schema: "audit",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChainHead", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Anchor_TenantId",
                schema: "audit",
                table: "Anchor",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Anchor_TenantId_Sequence",
                schema: "audit",
                table: "Anchor",
                columns: new[] { "TenantId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId",
                schema: "audit",
                table: "AuditEvent",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_ActorId",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_CorrelationId",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_EntityType_EntityId",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_OccurredAt",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_Sequence",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChainHead_TenantId",
                schema: "audit",
                table: "ChainHead",
                column: "TenantId");
        
            // Immutability (ADR-0003): defence in depth. 1) the app role cannot UPDATE/DELETE audit events,
            // 2) a trigger rejects UPDATE/DELETE from any other principal, 3) the hash chain detects the rest.
            migrationBuilder.Sql("""
                CREATE TRIGGER [audit].[trg_AuditEvent_Immutable] ON [audit].[AuditEvent]
                AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                    THROW 51000, 'Audit events are append-only and cannot be updated or deleted.', 1;
                END
                """);
            migrationBuilder.Sql($"GRANT SELECT, INSERT ON [audit].[AuditEvent] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY UPDATE, DELETE ON [audit].[AuditEvent] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"GRANT SELECT, INSERT, UPDATE ON [audit].[ChainHead] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY DELETE ON [audit].[ChainHead] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"GRANT SELECT, INSERT ON [audit].[Anchor] TO [{TenantSecurityMigrationExtensions.AppRole}];");
            migrationBuilder.Sql($"DENY UPDATE, DELETE ON [audit].[Anchor] TO [{TenantSecurityMigrationExtensions.AppRole}];");

            migrationBuilder.EnableTenantRowLevelSecurity("audit", "AuditEvent");
            migrationBuilder.EnableTenantRowLevelSecurity("audit", "ChainHead");
            migrationBuilder.EnableTenantRowLevelSecurity("audit", "Anchor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("audit", "Anchor");
            migrationBuilder.DisableTenantRowLevelSecurity("audit", "ChainHead");
            migrationBuilder.DisableTenantRowLevelSecurity("audit", "AuditEvent");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [audit].[trg_AuditEvent_Immutable];");

            migrationBuilder.DropTable(
                name: "Anchor",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "AuditEvent",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "ChainHead",
                schema: "audit");
        }
    }
}
