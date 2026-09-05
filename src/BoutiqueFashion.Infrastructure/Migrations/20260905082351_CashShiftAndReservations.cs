using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoutiqueFashion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CashShiftAndReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "QuantityReserved",
                table: "ProductVariants",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ClosedBy",
                table: "CashSessions",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatorName",
                table: "CashSessions",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OperatorPinHash",
                table: "CashSessions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuantityReserved",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "ClosedBy",
                table: "CashSessions");

            migrationBuilder.DropColumn(
                name: "OperatorName",
                table: "CashSessions");

            migrationBuilder.DropColumn(
                name: "OperatorPinHash",
                table: "CashSessions");
        }
    }
}
