using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.Invoices.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Approval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ApprovalRunId",
                schema: "invoice",
                table: "Invoice",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                schema: "invoice",
                table: "Invoice",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedVersion",
                schema: "invoice",
                table: "Invoice",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalRunId",
                schema: "invoice",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                schema: "invoice",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ApprovedVersion",
                schema: "invoice",
                table: "Invoice");
        }
    }
}
