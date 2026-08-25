using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TesserChat.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_role_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_actor_account_id",
                table: "audit_entries",
                column: "actor_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_target_account_id",
                table: "audit_entries",
                column: "target_account_id");

            // Append-only, enforced by the database rather than trusted to the code above it
            // (§5.5). EF has no model concept for this, so it is written by hand here.
            //
            // DO INSTEAD NOTHING makes an UPDATE or DELETE against this table a silent no-op rather
            // than an error: rules rewrite the statement, they do not raise. That is the behaviour
            // wanted here — a stray write changes nothing — and the tests assert the rows survive
            // rather than that an exception was thrown.
            //
            // Retention pruning, if it is ever wanted, means a migration that deliberately drops
            // these and recreates them afterwards. That friction is the point: deleting an audit
            // trail should be a decision someone writes down, not a DELETE someone runs.
            migrationBuilder.Sql(
                """
                CREATE RULE audit_entries_no_update AS
                    ON UPDATE TO audit_entries DO INSTEAD NOTHING;
                """);

            migrationBuilder.Sql(
                """
                CREATE RULE audit_entries_no_delete AS
                    ON DELETE TO audit_entries DO INSTEAD NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropped before the table so that reverting is not itself blocked by the rules. DROP
            // TABLE removes them anyway; being explicit says the ordering was considered.
            migrationBuilder.Sql("DROP RULE IF EXISTS audit_entries_no_delete ON audit_entries;");
            migrationBuilder.Sql("DROP RULE IF EXISTS audit_entries_no_update ON audit_entries;");

            migrationBuilder.DropTable(
                name: "audit_entries");
        }
    }
}
