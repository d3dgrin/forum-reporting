using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Account.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125104413_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "accounts",
            columns: table => new
            {
                user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                block_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_accounts", x => x.user_id);
            });

        migrationBuilder.CreateTable(
            name: "processed_messages",
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_processed_messages", x => x.event_id);
            });

        migrationBuilder.InsertData(
            table: "accounts",
            columns: new[] { "user_id", "block_reason", "blocked_at", "status" },
            values: new object[,]
            {
                { "reporter-1", null, null, "Active" },
                { "reporter-2", null, null, "Active" },
                { "reporter-3", null, null, "Active" },
                { "reporter-4", null, null, "Active" },
                { "reporter-5", null, null, "Active" },
                { "reporter-6", null, null, "Active" },
                { "target-1", null, null, "Active" },
                { "target-2", null, null, "Active" }
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "accounts");

        migrationBuilder.DropTable(
            name: "processed_messages");
    }
}
