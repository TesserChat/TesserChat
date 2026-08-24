using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TesserChat.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FirstRunSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "server_instances",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "set_up_at",
                table: "server_instances",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "set_up_by_account_id",
                table: "server_instances",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "singleton",
                table: "server_instances",
                type: "boolean",
                nullable: false,
                defaultValueSql: "true");

            migrationBuilder.CreateIndex(
                name: "ix_server_instances_singleton",
                table: "server_instances",
                column: "singleton",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_server_instances_singleton",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "name",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "set_up_at",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "set_up_by_account_id",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "singleton",
                table: "server_instances");
        }
    }
}
