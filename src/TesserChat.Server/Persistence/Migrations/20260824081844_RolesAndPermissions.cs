using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TesserChat.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RolesAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permissions", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_system_role = table.Column<bool>(type: "boolean", nullable: false),
                    is_owner = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "account_roles",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_roles", x => new { x.account_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_account_roles_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_account_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission_key });
                    table.ForeignKey(
                        name: "fk_role_permissions_permissions_permission_key",
                        column: x => x.permission_key,
                        principalTable: "permissions",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "permissions",
                columns: new[] { "key", "description" },
                values: new object[,]
                {
                    { "auditlog.read", "Read the record of moderation and administration actions." },
                    { "members.ban", "Ban a member and prevent that key from registering again." },
                    { "members.kick", "Remove a member from this server." },
                    { "messages.delete", "Delete messages posted by other members." },
                    { "roles.assign", "Assign roles to members and remove them." },
                    { "roles.manage", "Create, edit, and delete roles and the permissions they grant." },
                    { "server.manage", "Change server settings, including how new members may join." }
                });

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "created_at", "is_owner", "is_system_role", "name" },
                values: new object[,]
                {
                    { new Guid("9a4d151d-c7d9-8580-b363-66b8bc7b19e0"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, true, "Admin" },
                    { new Guid("bd8ddf38-0605-8dd8-bc39-fed582c9b020"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, true, "Member" },
                    { new Guid("d7b20ae6-0d3b-86ec-9559-d159581d70c5"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, true, "Owner" }
                });

            migrationBuilder.InsertData(
                table: "role_permissions",
                columns: new[] { "permission_key", "role_id" },
                values: new object[,]
                {
                    { "auditlog.read", new Guid("9a4d151d-c7d9-8580-b363-66b8bc7b19e0") },
                    { "members.ban", new Guid("9a4d151d-c7d9-8580-b363-66b8bc7b19e0") },
                    { "members.kick", new Guid("9a4d151d-c7d9-8580-b363-66b8bc7b19e0") },
                    { "messages.delete", new Guid("9a4d151d-c7d9-8580-b363-66b8bc7b19e0") },
                    { "roles.assign", new Guid("9a4d151d-c7d9-8580-b363-66b8bc7b19e0") }
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_roles_account_id",
                table: "account_roles",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_roles_role_id",
                table: "account_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_permission_key",
                table: "role_permissions",
                column: "permission_key");

            migrationBuilder.CreateIndex(
                name: "ix_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_single_owner",
                table: "roles",
                column: "is_owner",
                unique: true,
                filter: "is_owner");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_roles");

            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
