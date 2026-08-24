using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TesserChat.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    signing_key = table.Column<byte[]>(type: "bytea", fixedLength: true, maxLength: 32, nullable: false),
                    encryption_key = table.Column<byte[]>(type: "bytea", fixedLength: true, maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_signing_key",
                table: "accounts",
                column: "signing_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts");
        }
    }
}
