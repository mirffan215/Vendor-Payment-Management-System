using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Invoice_TenantId_CreatedAt_Id",
                schema: "invoice",
                table: "Invoice",
                columns: new[] { "TenantId", "CreatedAt", "Id" },
                descending: new[] { false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoice_TenantId_CreatedAt_Id",
                schema: "invoice",
                table: "Invoice");
        }
    }
}
