using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reporting.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125102352_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "outbox_messages",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false),
                last_error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_outbox_messages", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "reported_user_counters",
            columns: table => new
            {
                reported_user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                unique_report_count = table.Column<int>(type: "integer", nullable: false),
                block_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_reported_user_counters", x => x.reported_user_id);
                table.CheckConstraint("ck_reported_user_counters_count_positive", "unique_report_count > 0");
            });

        migrationBuilder.CreateTable(
            name: "reports",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                reporter_user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                reported_user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                post_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_reports", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_outbox_messages_unpublished",
            table: "outbox_messages",
            column: "created_at",
            filter: "published_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_reports_reported_user_id",
            table: "reports",
            column: "reported_user_id");

        migrationBuilder.CreateIndex(
            name: "ux_reports_reporter_reported",
            table: "reports",
            columns: new[] { "reporter_user_id", "reported_user_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "outbox_messages");

        migrationBuilder.DropTable(
            name: "reported_user_counters");

        migrationBuilder.DropTable(
            name: "reports");
    }
}
