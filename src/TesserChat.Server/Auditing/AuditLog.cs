using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Persistence;

namespace TesserChat.Server.Auditing;

/// <summary>
/// Records and reads moderation and administration actions (§5.5).
/// </summary>
/// <remarks>
/// <para>
/// The surface is append and read. There is no update and no delete, matching what the database
/// itself permits — see <see cref="AuditEntry"/> for why that is enforced there rather than trusted
/// here.
/// </para>
/// <para>
/// <b>Recording is part of the action, not a reaction to it.</b> Callers append inside the same
/// transaction as the change they are describing, so a committed change always has its entry and a
/// rolled-back one leaves none. An audit log written afterwards, on a best-effort basis, is missing
/// exactly the entries that matter most — the ones where something went wrong.
/// </para>
/// </remarks>
internal sealed class AuditLog(TesserChatDbContext context, TimeProvider timeProvider)
{
    /// <summary>
    /// Records an action.
    /// </summary>
    /// <remarks>
    /// Adds to the change tracker without saving. The caller saves, which is what puts the entry in
    /// the same transaction as the change it describes.
    /// </remarks>
    /// <param name="action">What happened.</param>
    /// <param name="detail">
    /// A short human-readable summary, kept readable after the things it names are gone. Never a
    /// secret — see <see cref="AuditEntry.Detail"/>.
    /// </param>
    /// <param name="actorAccountId">
    /// Who did it, or <see langword="null"/> when the server itself did — first-run setup has no
    /// account to attribute to yet.
    /// </param>
    /// <param name="targetAccountId">The account acted on, if any.</param>
    /// <param name="targetRoleId">The role acted on, if any.</param>
    public void Record(
        AuditAction action,
        string detail,
        Guid? actorAccountId = null,
        Guid? targetAccountId = null,
        Guid? targetRoleId = null)
    {
        context.AuditEntries.Add(new AuditEntry
        {
            Action = action,
            OccurredAt = timeProvider.GetUtcNow(),
            ActorAccountId = actorAccountId,
            TargetAccountId = targetAccountId,
            TargetRoleId = targetRoleId,
            Detail = Truncate(detail),
        });
    }

    /// <summary>
    /// Reads the most recent entries, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered by <see cref="AuditEntry.Id"/> rather than timestamp: the id is a sequence, so it
    /// orders actions recorded in the same transaction, which share a timestamp.
    /// </para>
    /// <para>
    /// Reading requires <c>auditlog.read</c> (§5.3). This class does not check that — the caller
    /// does, at the endpoint, the same split <c>RoleManager</c> uses.
    /// </para>
    /// </remarks>
    /// <param name="limit">How many entries to return, capped at <see cref="MaxPageSize"/>.</param>
    /// <param name="before">
    /// Return only entries older than this id, for paging back through the log.
    /// </param>
    public async Task<IReadOnlyList<AuditEntry>> ReadAsync(
        int limit = DefaultPageSize,
        long? before = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.AuditEntries.AsNoTracking();

        if (before is { } cursor)
        {
            query = query.Where(entry => entry.Id < cursor);
        }

        return await query
            .OrderByDescending(entry => entry.Id)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the entries concerning one account, newest first — as actor or as target.
    /// </summary>
    /// <remarks>
    /// Both directions, because "what did this moderator do" and "what was done to this member" are
    /// the two questions actually asked of an audit log, and answering only one of them means
    /// reading the whole table to answer the other.
    /// </remarks>
    public async Task<IReadOnlyList<AuditEntry>> ReadForAccountAsync(
        Guid accountId,
        int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
        => await context.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.ActorAccountId == accountId || entry.TargetAccountId == accountId)
            .OrderByDescending(entry => entry.Id)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken);

    /// <summary>Entries returned when a caller does not say.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Most entries one read can return.</summary>
    /// <remarks>
    /// A cap rather than a suggestion: the log only grows, so an uncapped read becomes slower every
    /// day it runs and eventually stops returning at all.
    /// </remarks>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Trims a detail to what the column holds.
    /// </summary>
    /// <remarks>
    /// Truncates rather than refusing. A detail is a description of something that already happened,
    /// so rejecting one would mean either losing the entry entirely or failing the action it
    /// describes — both worse than a clipped sentence.
    /// </remarks>
    private static string Truncate(string? detail)
    {
        var trimmed = detail?.Trim() ?? string.Empty;

        return trimmed.Length <= AuditEntry.DetailMaxLength
            ? trimmed
            : trimmed[..(AuditEntry.DetailMaxLength - 1)] + '…';
    }
}
