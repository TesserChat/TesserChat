using TesserChat.Server.Auditing;

namespace TesserChat.Server.Persistence;

/// <summary>
/// One recorded moderation or administration action (§5.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, enforced by the database.</b> Postgres rules reject UPDATE and DELETE on this
/// table, so the trail holds against a future code path that forgets and against anyone with a
/// connection to the database — an audit log a moderator can quietly edit is not an audit log.
/// Pruning for retention later means a migration that deliberately drops and recreates those rules,
/// which is the right amount of friction for deleting an audit trail.
/// </para>
/// <para>
/// <b>The account ids are plain values, not foreign keys.</b> Every other join in this schema
/// cascades on account deletion; this one must not, or deleting an account would erase the record
/// of what it did — the obvious way to cover tracks. An id whose account is gone stops resolving to
/// a display name, which is exactly what a deleted account should look like.
/// </para>
/// </remarks>
internal sealed class AuditEntry
{
    /// <summary>Longest a serialised detail payload may be.</summary>
    public const int DetailMaxLength = 1024;

    /// <summary>
    /// Monotonic identifier, assigned by the database.
    /// </summary>
    /// <remarks>
    /// A sequence rather than a derived or random id: the audit log is read in the order things
    /// happened, and a gap in the sequence is itself evidence. Ordering by timestamp alone would
    /// tie for actions in the same transaction.
    /// </remarks>
    public long Id { get; init; }

    /// <summary>What happened.</summary>
    public AuditAction Action { get; init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// The account that performed the action (§5.1).
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> for an action no account performed — first-run setup happens before
    /// any account exists to attribute it to, and a future scheduled task would be the same. Null
    /// means "the server did this", never "we do not know who did this".
    /// </remarks>
    public Guid? ActorAccountId { get; init; }

    /// <summary>The account the action was performed on, when there is one.</summary>
    public Guid? TargetAccountId { get; init; }

    /// <summary>The role involved, for role actions.</summary>
    public Guid? TargetRoleId { get; init; }

    /// <summary>
    /// A short human-readable summary of what changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately denormalised text rather than a join. An audit entry has to stay readable after
    /// the things it refers to are gone: "granted Moderator" still means something once the
    /// Moderator role is deleted, whereas a dangling role id does not.
    /// </para>
    /// <para>
    /// Never put a secret here. This is read by anyone holding <c>auditlog.read</c>, and joining
    /// passwords, tokens, and key material have no business in it.
    /// </para>
    /// </remarks>
    public string Detail { get; init; } = string.Empty;
}
