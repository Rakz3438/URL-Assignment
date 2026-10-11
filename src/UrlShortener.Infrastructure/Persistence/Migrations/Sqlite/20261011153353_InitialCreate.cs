using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UrlShortener.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "click_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferrerHost = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    UserAgentFamily = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    VisitorHash = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_click_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "short_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsCustomAlias = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_short_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_click_events_Code_OccurredAt",
                table: "click_events",
                columns: new[] { "Code", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_short_links_Code",
                table: "short_links",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_short_links_IdempotencyKey",
                table: "short_links",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "click_events");

            migrationBuilder.DropTable(
                name: "short_links");
        }
    }
}
