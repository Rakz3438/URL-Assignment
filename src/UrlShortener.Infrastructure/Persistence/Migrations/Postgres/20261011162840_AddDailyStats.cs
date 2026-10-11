using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UrlShortener.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddDailyStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "link_daily_stats",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Clicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_daily_stats", x => new { x.Code, x.Day });
                });

            // Hand-written data migration (brownfield): aggregate clicks recorded by the old synchronous
            // path so totals stay correct after cut-over. Idempotent via ON CONFLICT.
            migrationBuilder.Sql("""
                INSERT INTO link_daily_stats ("Code", "Day", "Clicks")
                SELECT "Code", ("OccurredAt" AT TIME ZONE 'UTC')::date, COUNT(*)
                FROM click_events
                GROUP BY "Code", ("OccurredAt" AT TIME ZONE 'UTC')::date
                ON CONFLICT ("Code", "Day") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link_daily_stats");
        }
    }
}
