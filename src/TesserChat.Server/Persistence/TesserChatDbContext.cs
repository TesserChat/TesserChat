using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Auditing;
using TesserChat.Server.Authorization;
using TesserChat.Shared.Auth;
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

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<LoginNonce> LoginNonces => Set<LoginNonce>();

    public DbSet<TokenSigningKey> TokenSigningKeys => Set<TokenSigningKey>();

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<RoomMembership> RoomMemberships => Set<RoomMembership>();

    public DbSet<RoomMessage> RoomMessages => Set<RoomMessage>();

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

            entity.Property(instance => instance.Name)
                .IsRequired()
                .HasMaxLength(ServerInstance.NameMaxLength);

            entity.Property(instance => instance.SetUpAt).IsRequired();
            entity.Property(instance => instance.SetUpByAccountId).IsRequired();

            // At most one row, ever. Setup completing is defined as this row existing (§5.6), so
            // "setup runs once" has to be a property of the database rather than of the code that
            // happens to check first — otherwise two clients racing on a fresh server could both
            // pass their check and both claim Owner.
            //
            // A one-column table with a constant default and a unique index on it: the second
            // insert collides on the index whatever id it carries. Expressed on a shadow property
            // so nothing in the model has to carry a column that means nothing to it, and given a
            // default in the store so EF never sends a value for it.
            entity.Property<bool>("singleton")
                .HasColumnName("singleton")
                .HasDefaultValueSql("true")
                .ValueGeneratedOnAdd()
                // Sentinel `true`: EF skips a property whose value equals the sentinel and lets the
                // store default apply. Without this EF warns on every startup that it cannot tell
                // an unset bool from a deliberate `false` — true here, but only because nothing
                // ever sets this property, which is what the sentinel says.
                .HasSentinel(true);

            entity.HasIndex("singleton")
                .IsUnique()
                .HasDatabaseName("ix_server_instances_singleton");
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

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(audit => audit.Id);

            // A sequence: the log is read in the order things happened, and a gap is evidence.
            entity.Property(audit => audit.Id).ValueGeneratedOnAdd();

            // Stored as the member name, so a row read in psql says RoleGranted rather than 3, and
            // renumbering the enum can never reinterpret existing history.
            entity.Property(audit => audit.Action)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(64);

            entity.Property(audit => audit.OccurredAt).IsRequired();

            entity.Property(audit => audit.Detail)
                .IsRequired()
                .HasMaxLength(AuditEntry.DetailMaxLength);

            // No foreign keys to accounts or roles, deliberately. Everything else in this schema
            // cascades on account deletion; an audit trail that did would let a moderator erase
            // what they did by deleting the account that did it. See AuditEntry.
            entity.Property(audit => audit.ActorAccountId);
            entity.Property(audit => audit.TargetAccountId);
            entity.Property(audit => audit.TargetRoleId);

            // "What did this account do, or have done to it" is the question an audit log is asked,
            // so both directions are indexed rather than only the actor.
            entity.HasIndex(audit => audit.ActorAccountId);
            entity.HasIndex(audit => audit.TargetAccountId);
        });

        modelBuilder.Entity<LoginNonce>(entity =>
        {
            // The nonce value is the key. A surrogate id would let the same bytes be inserted
            // twice, which is precisely what this table exists to make impossible (§4.7).
            entity.HasKey(challenge => challenge.Value);

            entity.Property(challenge => challenge.Value)
                .HasMaxLength(LoginChallenge.NonceSize)
                .IsFixedLength();

            entity.Property(challenge => challenge.IssuedAt).IsRequired();
            entity.Property(challenge => challenge.ExpiresAt).IsRequired();

            // Null means outstanding, so this is deliberately nullable rather than a bool plus a
            // timestamp that could disagree with it.
            entity.Property(challenge => challenge.ConsumedAt);

            // The sweep deletes by expiry across the whole table (§4.7), which is the only query
            // here that is not a primary-key lookup.
            entity.HasIndex(challenge => challenge.ExpiresAt);
        });

        modelBuilder.Entity<TokenSigningKey>(entity =>
        {
            entity.HasKey(key => key.Id);

            // The id travels in the token's `kid` header and is generated when the key is, so the
            // server knows it without reading the row back.
            entity.Property(key => key.Id).ValueGeneratedNever();

            entity.Property(key => key.Secret)
                .IsRequired()
                .HasMaxLength(TokenSigningKey.SecretSize)
                .IsFixedLength();

            entity.Property(key => key.CreatedAt).IsRequired();

            // Null means current. Nullable rather than a bool beside a timestamp, for the same
            // reason as LoginNonce.ConsumedAt: two columns can disagree, one cannot.
            entity.Property(key => key.RetiredAt);

            // Choosing the signing key reads the newest unretired row on a cache miss, which is the
            // only query here that is not a primary-key lookup by `kid`.
            entity.HasIndex(key => key.CreatedAt);
        });

        modelBuilder.Entity<Room>(entity =>
        {
            entity.HasKey(room => room.Id);

            // Generated by the server when the room is created, not by Postgres, so the creating
            // code knows the id without reading the row back.
            entity.Property(room => room.Id).ValueGeneratedNever();

            entity.Property(room => room.Name)
                .IsRequired()
                .HasMaxLength(Room.NameMaxLength);

            entity.Property(room => room.Topic)
                .IsRequired()
                .HasMaxLength(Room.TopicMaxLength);

            entity.Property(room => room.CreatedAt).IsRequired();

            // Not a foreign key, deliberately: a room outlives the account that created it. See
            // Room.CreatedByAccountId.
            entity.Property(room => room.CreatedByAccountId);

            // One room per name, enforced by Postgres rather than by whichever code path happens
            // to check first — two clients creating "general" at once must not both succeed.
            entity.HasIndex(room => room.Name).IsUnique();
        });

        modelBuilder.Entity<RoomMembership>(entity =>
        {
            entity.HasKey(membership => new { membership.RoomId, membership.AccountId });

            entity.Property(membership => membership.JoinedAt).IsRequired();

            // A deleted room takes its memberships with it, and so does a deleted account. Neither
            // leaves a row pointing at nothing.
            entity.HasOne(membership => membership.Room)
                .WithMany(room => room.Members)
                .HasForeignKey(membership => membership.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(membership => membership.Account)
                .WithMany()
                .HasForeignKey(membership => membership.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // "Which rooms is this account in" is what the client asks on connect, so the index
            // matches that query rather than the key order.
            entity.HasIndex(membership => membership.AccountId);
        });

        modelBuilder.Entity<RoomMessage>(entity =>
        {
            entity.HasKey(message => message.Id);

            // A sequence: history is ordered and paged by this. See RoomMessage.Id.
            entity.Property(message => message.Id).ValueGeneratedOnAdd();

            entity.Property(message => message.PostedAt).IsRequired();

            entity.Property(message => message.Body)
                .IsRequired()
                .HasMaxLength(RoomMessage.BodyMaxLength);

            // Deleting a room deletes its messages: the history belongs to the room, and there is
            // nowhere for a message to be read once its room is gone.
            entity.HasOne(message => message.Room)
                .WithMany(room => room.Messages)
                .HasForeignKey(message => message.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting an *account* must not delete what they said. A room's history is a shared
            // record that other members' conversations refer to, and cascading here would let one
            // member punch holes in everyone else's context by deleting their account — the same
            // reasoning that keeps AuditEntry's account ids out of foreign keys, applied to a table
            // that does need the join for display names.
            //
            // Restrict rather than SetNull: the author is what §5.1 makes authorship mean, so a
            // message with no author is not a state this table should be able to hold. Deleting an
            // account that has posted is therefore refused by Postgres until the caller decides
            // what should happen — which is a decision for the kick/ban work (#48), not a default
            // silently chosen here.
            entity.HasOne(message => message.Author)
                .WithMany()
                .HasForeignKey(message => message.AuthorAccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // The one query history is served by: a room's messages, newest first, paged by id.
            // Descending because the first page a client asks for is the most recent one.
            entity.HasIndex(message => new { message.RoomId, message.Id })
                .IsDescending(false, true);
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
