using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupplierTaxNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupplierTaxNumber",
                schema: "invoice",
                table: "Invoice",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupplierTaxNumber",
                schema: "invoice",
                table: "Invoice");
        }
    }
}
