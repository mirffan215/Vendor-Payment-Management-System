using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DedupeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Notification_TenantId_DedupeKey",
                schema: "notify",
                table: "Notification",
                columns: new[] { "TenantId", "DedupeKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notification_TenantId_DedupeKey",
                schema: "notify",
                table: "Notification");
        }
    }
}
