using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notify");

            migrationBuilder.CreateTable(
                name: "Notification",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubjectLabel = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationCursor",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationCursor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreference",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreference", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTemplate",
                schema: "notify",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTemplate", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_TenantId_UserId_DedupeKey",
                schema: "notify",
                table: "Notification",
                columns: new[] { "TenantId", "UserId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notification_TenantId_UserId_ReadAt_CreatedAt",
                schema: "notify",
                table: "Notification",
                columns: new[] { "TenantId", "UserId", "ReadAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationCursor_TenantId_Source",
                schema: "notify",
                table: "NotificationCursor",
                columns: new[] { "TenantId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreference_TenantId_UserId_Kind_Channel",
                schema: "notify",
                table: "NotificationPreference",
                columns: new[] { "TenantId", "UserId", "Kind", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplate_TenantId_Kind_Channel_Locale",
                schema: "notify",
                table: "NotificationTemplate",
                columns: new[] { "TenantId", "Kind", "Channel", "Locale" },
                unique: true);

            foreach (var table in new[] { "Notification", "NotificationCursor", "NotificationPreference", "NotificationTemplate" })
            {
                migrationBuilder.EnableTenantRowLevelSecurity("notify", table);
            }

            migrationBuilder.GrantAppRoleOnSchema("notify");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "Notification", "NotificationCursor", "NotificationPreference", "NotificationTemplate" })
            {
                migrationBuilder.DisableTenantRowLevelSecurity("notify", table);
            }

            migrationBuilder.DropTable(
                name: "Notification",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "NotificationCursor",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "NotificationPreference",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "NotificationTemplate",
                schema: "notify");
        }
    }
}
