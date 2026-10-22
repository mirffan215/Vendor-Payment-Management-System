using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Delivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelConfig",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
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
                    table.PrimaryKey("PK_ChannelConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDelivery",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDelivery", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConfig_TenantId_Channel_VersionNo",
                schema: "notify",
                table: "ChannelConfig",
                columns: new[] { "TenantId", "Channel", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ChannelConfig_Pending",
                schema: "notify",
                table: "ChannelConfig",
                columns: new[] { "TenantId", "Channel" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_TenantId_NotificationId_Channel",
                schema: "notify",
                table: "NotificationDelivery",
                columns: new[] { "TenantId", "NotificationId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_TenantId_Status_NextAttemptAt",
                schema: "notify",
                table: "NotificationDelivery",
                columns: new[] { "TenantId", "Status", "NextAttemptAt" });

            foreach (var table in new[] { "NotificationDelivery", "ChannelConfig" })
            {
                migrationBuilder.EnableTenantRowLevelSecurity("notify", table);
            }

            migrationBuilder.GrantAppRoleOnSchema("notify");

            // Superseded channel versions show where messages used to go; they are never deleted.
            migrationBuilder.Sql($"DENY DELETE ON [notify].[ChannelConfig] TO [{TenantSecurityMigrationExtensions.AppRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "NotificationDelivery", "ChannelConfig" })
            {
                migrationBuilder.DisableTenantRowLevelSecurity("notify", table);
            }

            migrationBuilder.DropTable(
                name: "ChannelConfig",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "NotificationDelivery",
                schema: "notify");
        }
    }
}
