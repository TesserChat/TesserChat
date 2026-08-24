using Microsoft.EntityFrameworkCore;
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
    }
}
