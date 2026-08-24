using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Authorization;
using TesserChat.Shared.Identity;

namespace TesserChat.Server.Persistence;

/// <summary>
/// The server's single EF Core context over its PostgreSQL database (§5.4).
/// </summary>
/// <remarks>
/// Table and column names are snake_case, applied by convention rather than per-property
/// attributes. Postgres folds unquoted identifiers to lowercase, so EF's default PascalCase names
/// would have to be double-quoted in every hand-written query an operator ever runs against their
/// own database. That is a poor default for a self-hosted product, and switching later would mean
/// a rename migration across every table.
/// </remarks>
internal sealed class TesserChatDbContext(DbContextOptions<TesserChatDbContext> options)
    : DbContext(options)
{
    public DbSet<ServerInstance> ServerInstances => Set<ServerInstance>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<PermissionGrant> Permissions => Set<PermissionGrant>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<AccountRole> AccountRoles => Set<AccountRole>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ServerInstance>(entity =>
        {
            entity.HasKey(instance => instance.Id);

            // No value generation: the id is assigned by the setup flow (§5.6), not the database,
            // so that the server knows its own identity without a round-trip to read it back.
            entity.Property(instance => instance.Id).ValueGeneratedNever();

            entity.Property(instance => instance.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(account => account.Id);

            // Derived from the signing key (§5.1), so the database must never invent one.
            entity.Property(account => account.Id).ValueGeneratedNever();

            entity.Property(account => account.SigningKey)
                .IsRequired()
                .HasMaxLength(IdentityKeyPair.PublicKeySize)
                .IsFixedLength();

            entity.Property(account => account.EncryptionKey)
                .IsRequired()
                .HasMaxLength(IdentityKeyPair.PublicKeySize)
                .IsFixedLength();

            entity.Property(account => account.DisplayName)
                .IsRequired()
                .HasMaxLength(Account.DisplayNameMaxLength);

            entity.Property(account => account.RegisteredAt).IsRequired();

            // The id is a truncated hash of this key (§5.1), so the primary key already collides
            // for a repeat registration. This states the real rule directly rather than leaving it
            // implied by the hash: one account per signing key, enforced by Postgres even if a
            // future id scheme changes.
            entity.HasIndex(account => account.SigningKey).IsUnique();
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(role => role.Id);
            entity.Property(role => role.Id).ValueGeneratedNever();

            entity.Property(role => role.Name)
                .IsRequired()
                .HasMaxLength(Role.NameMaxLength);

            entity.Property(role => role.IsSystemRole).IsRequired();
            entity.Property(role => role.IsOwner).IsRequired();
            entity.Property(role => role.CreatedAt).IsRequired();

            // One role per name, so a member reading "Moderator" in two places reads about one role.
            entity.HasIndex(role => role.Name).IsUnique();

            // At most one Owner role, enforced by the database rather than by the code that happens
            // to create roles. A partial index is how Postgres expresses "unique among the rows
            // where this is true".
            entity.HasIndex(role => role.IsOwner)
                .IsUnique()
                .HasFilter("is_owner")
                .HasDatabaseName("ix_roles_single_owner");
        });

        modelBuilder.Entity<PermissionGrant>(entity =>
        {
            // The key is the identity; a surrogate id would add a join without adding meaning.
            entity.HasKey(permission => permission.Key);

            entity.Property(permission => permission.Key)
                .HasMaxLength(Permission.KeyMaxLength);

            entity.Property(permission => permission.Description)
                .IsRequired()
                .HasMaxLength(Permission.DescriptionMaxLength);

            entity.HasData(Permission.All.Select(permission => new
            {
                permission.Key,
                permission.Description,
            }));
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(grant => new { grant.RoleId, grant.PermissionKey });

            entity.Property(grant => grant.PermissionKey).HasMaxLength(Permission.KeyMaxLength);

            // Deleting a role withdraws its grants. Deleting a permission is not something the
            // server does at runtime, but the cascade keeps a removed key from stranding rows.
            entity.HasOne(grant => grant.Role)
                .WithMany(role => role.Permissions)
                .HasForeignKey(grant => grant.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(grant => grant.Permission)
                .WithMany(permission => permission.Roles)
                .HasForeignKey(grant => grant.PermissionKey)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasData(SeedRolePermissions());
        });

        modelBuilder.Entity<AccountRole>(entity =>
        {
            entity.HasKey(assignment => new { assignment.AccountId, assignment.RoleId });

            entity.Property(assignment => assignment.GrantedAt).IsRequired();

            // A removed account takes its assignments with it; so does a deleted role. Neither
            // leaves a row pointing at nothing.
            entity.HasOne(assignment => assignment.Account)
                .WithMany(account => account.Roles)
                .HasForeignKey(assignment => assignment.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(assignment => assignment.Role)
                .WithMany(role => role.Members)
                .HasForeignKey(assignment => assignment.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            // Answering "what may this account do" reads every assignment it holds, so the index
            // matches the query rather than the key order.
            entity.HasIndex(assignment => assignment.AccountId);
        });

        SeedSystemRoles(modelBuilder);
    }

    /// <summary>
    /// Seeds the three default roles (§5.3).
    /// </summary>
    /// <remarks>
    /// Seeded through the migration rather than at startup, so a fresh database has them before
    /// anything runs and an existing one is not re-checked on every boot. Ids are derived from the
    /// role names, so they are identical in every deployment and stable across migration rebuilds.
    /// </remarks>
    private static void SeedSystemRoles(ModelBuilder modelBuilder)
    {
        // Fixed rather than "now": a seeded timestamp has to be constant, or every rebuild of the
        // migration produces a different one and the model is never settled.
        var seededAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        modelBuilder.Entity<Role>().HasData(
            new
            {
                Id = SystemRoles.OwnerId,
                Name = SystemRoles.OwnerName,
                IsSystemRole = true,
                IsOwner = true,
                CreatedAt = seededAt,
            },
            new
            {
                Id = SystemRoles.AdminId,
                Name = SystemRoles.AdminName,
                IsSystemRole = true,
                IsOwner = false,
                CreatedAt = seededAt,
            },
            new
            {
                Id = SystemRoles.MemberId,
                Name = SystemRoles.MemberName,
                IsSystemRole = true,
                IsOwner = false,
                CreatedAt = seededAt,
            });
    }

    /// <summary>
    /// The permission grants the seeded roles start with.
    /// </summary>
    /// <remarks>
    /// The Owner is deliberately absent: it holds every permission implicitly, so seeding rows for
    /// it would imply an administrator could take them away.
    /// </remarks>
    private static IEnumerable<object> SeedRolePermissions()
        => SystemRoles.AdminPermissions
            .Select(permission => new { RoleId = SystemRoles.AdminId, PermissionKey = permission.Key })
            .Concat(SystemRoles.MemberPermissions
                .Select(permission => new { RoleId = SystemRoles.MemberId, PermissionKey = permission.Key }));
}
