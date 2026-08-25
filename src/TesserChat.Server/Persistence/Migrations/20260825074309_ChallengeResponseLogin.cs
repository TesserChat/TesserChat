using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TesserChat.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChallengeResponseLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_nonces",
                columns: table => new
                {
                    value = table.Column<byte[]>(type: "bytea", fixedLength: true, maxLength: 32, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_nonces", x => x.value);
                });

            migrationBuilder.CreateIndex(
                name: "ix_login_nonces_expires_at",
                table: "login_nonces",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_nonces");
        }
    }
}
