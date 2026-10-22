using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalBaselineJson",
                schema: "invoice",
                table: "Invoice",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalBaselineJson",
                schema: "invoice",
                table: "Invoice");
        }
    }
}
