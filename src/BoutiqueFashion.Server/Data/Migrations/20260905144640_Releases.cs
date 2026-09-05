using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoutiqueFashion.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Releases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "Devices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AppVersionSince",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingVersion",
                table: "Devices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateError",
                table: "Devices",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReleaseAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sha1 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    NotesMarkdown = table.Column<string>(type: "text", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsWithdrawn = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReleaseTargets_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseAssets_Channel_Version",
                table: "ReleaseAssets",
                columns: new[] { "Channel", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseAssets_FileName",
                table: "ReleaseAssets",
                column: "FileName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseTargets_Shop",
                table: "ReleaseTargets",
                columns: new[] { "Channel", "Version", "ShopId" },
                unique: true,
                filter: "\"ShopId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseTargets_ShopId",
                table: "ReleaseTargets",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseTargets_Toutes",
                table: "ReleaseTargets",
                columns: new[] { "Channel", "Version" },
                unique: true,
                filter: "\"ShopId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReleaseAssets");

            migrationBuilder.DropTable(
                name: "ReleaseTargets");

            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AppVersionSince",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PendingVersion",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UpdateError",
                table: "Devices");
        }
    }
}
