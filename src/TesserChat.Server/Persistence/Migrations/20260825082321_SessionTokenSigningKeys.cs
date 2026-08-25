using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TesserChat.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionTokenSigningKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "token_signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret = table.Column<byte[]>(type: "bytea", fixedLength: true, maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_token_signing_keys", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_token_signing_keys_created_at",
                table: "token_signing_keys",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "token_signing_keys");
        }
    }
}
