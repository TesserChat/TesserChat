using Microsoft.EntityFrameworkCore;
using TesserChat.Server.Persistence;

namespace TesserChat.Server.Rooms;

/// <summary>
/// Creates rooms, moves members in and out of them, and stores and serves their messages (§5.4).
/// </summary>
/// <remarks>
/// <para>
/// This is where the room rules are enforced, in the same place and for the same reason as
/// <c>RoleManager</c>: in the mutation layer rather than in whatever client happens to be calling.
/// </para>
/// <list type="bullet">
/// <item>A room name is unique on this server.</item>
/// <item>Posting requires membership of the room; reading history does not (§5.4).</item>
/// <item>Authorship and timestamps are decided here, never accepted from the caller.</item>
/// </list>
/// <para>
/// <b>This class does not check whether the caller holds a permission.</b> That is
/// <c>PermissionResolver</c>'s job at the call site, exactly as with <c>RoleManager</c>. The
/// membership rule below is not a permission — it is a property of the room — which is why it does
/// live here.
/// </para>
/// <para>
/// <b>It also does not deliver anything.</b> Fan-out to connected members is the hub's business
/// (§6), and keeping it out of here is what lets room storage be tested without a transport and
/// lets the hub method that follows be a thin call onto this.
/// </para>
/// </remarks>
internal sealed class RoomManager(TesserChatDbContext context, TimeProvider timeProvider)
{
    /// <summary>
    /// Largest page of history a single call will return.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than the default, so a client asking for everything gets a bounded answer
    /// instead of a room's entire permanent history in one response. The client pages with the
    /// cursor in <see cref="MessagePage.NextBefore"/>.
    /// </remarks>
    public const int MaxPageSize = 100;

    /// <summary>Default page size when a caller does not ask for one.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Creates a room.
    /// </summary>
    /// <remarks>
    /// The creator is recorded but not joined: creating a room and being in it are separate acts,
    /// and an administrator setting up a server's channels should not end up a member of every one
    /// of them. A client that wants both calls <see cref="JoinAsync"/> after.
    /// </remarks>
    public async Task<(RoomMutationResult Result, Room? Room)> CreateRoomAsync(
        string name,
        string topic = "",
        Guid? createdByAccountId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormaliseName(name, out var normalised))
        {
            return (RoomMutationResult.Refused(RoomMutationStatus.InvalidName), null);
        }

        if (!TryNormaliseTopic(topic, out var normalisedTopic))
        {
            return (RoomMutationResult.Refused(RoomMutationStatus.InvalidTopic), null);
        }

        if (await context.Rooms.AnyAsync(room => room.Name == normalised, cancellationToken))
        {
            return (RoomMutationResult.Refused(RoomMutationStatus.InvalidName), null);
        }

        var created = new Room
        {
            Id = Guid.NewGuid(),
            Name = normalised,
            Topic = normalisedTopic,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedByAccountId = createdByAccountId,
        };

        context.Rooms.Add(created);
        await context.SaveChangesAsync(cancellationToken);

        return (RoomMutationResult.Applied(), created);
    }

    /// <summary>
    /// Renames a room, or changes its topic.
    /// </summary>
    /// <remarks>
    /// Renaming is safe because nothing keys off the name — messages reference
    /// <see cref="Room.Id"/>, so a renamed room keeps its history.
    /// </remarks>
    public async Task<RoomMutationResult> UpdateRoomAsync(
        Guid roomId,
        string? name = null,
        string? topic = null,
        CancellationToken cancellationToken = default)
    {
        var room = await context.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, cancellationToken);
        if (room is null)
        {
            return RoomMutationResult.Refused(RoomMutationStatus.NotFound);
        }

        var changed = false;

        if (name is not null)
        {
            if (!TryNormaliseName(name, out var normalised))
            {
                return RoomMutationResult.Refused(RoomMutationStatus.InvalidName);
            }

            if (!string.Equals(room.Name, normalised, StringComparison.Ordinal))
            {
                if (await context.Rooms.AnyAsync(
                    r => r.Name == normalised && r.Id != roomId,
                    cancellationToken))
                {
                    return RoomMutationResult.Refused(RoomMutationStatus.InvalidName);
                }

                room.Name = normalised;
                changed = true;
            }
        }

        if (topic is not null)
        {
            if (!TryNormaliseTopic(topic, out var normalisedTopic))
            {
                return RoomMutationResult.Refused(RoomMutationStatus.InvalidTopic);
            }

            if (!string.Equals(room.Topic, normalisedTopic, StringComparison.Ordinal))
            {
                room.Topic = normalisedTopic;
                changed = true;
            }
        }

        if (!changed)
        {
            return RoomMutationResult.NoChange();
        }

        await context.SaveChangesAsync(cancellationToken);
        return RoomMutationResult.Applied();
    }

    /// <summary>
    /// Deletes a room and, with it, its messages.
    /// </summary>
    /// <remarks>
    /// The messages go because the history belongs to the room and there is nowhere to read it once
    /// the room is gone; the cascade is declared on the model rather than done by hand here. This
    /// is the one place room history is not permanent, and it takes a deliberate act to reach.
    /// </remarks>
    public async Task<RoomMutationResult> DeleteRoomAsync(
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        var room = await context.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, cancellationToken);
        if (room is null)
        {
            return RoomMutationResult.Refused(RoomMutationStatus.NotFound);
        }

        context.Rooms.Remove(room);
        await context.SaveChangesAsync(cancellationToken);

        return RoomMutationResult.Applied();
    }

    /// <summary>
    /// Joins an account to a room.
    /// </summary>
    /// <remarks>
    /// Joining a room the account is already in is not an error — it succeeds having changed
    /// nothing, so a client that retries a join it is unsure about does not have to distinguish the
    /// two cases.
    /// </remarks>
    public async Task<RoomMutationResult> JoinAsync(
        Guid roomId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (!await context.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken))
        {
            return RoomMutationResult.Refused(RoomMutationStatus.NotFound);
        }

        if (!await context.Accounts.AnyAsync(account => account.Id == accountId, cancellationToken))
        {
            return RoomMutationResult.Refused(RoomMutationStatus.NotFound);
        }

        var existing = await context.RoomMemberships.FirstOrDefaultAsync(
            membership => membership.RoomId == roomId && membership.AccountId == accountId,
            cancellationToken);

        if (existing is not null)
        {
            return RoomMutationResult.NoChange();
        }

        context.RoomMemberships.Add(new RoomMembership
        {
            RoomId = roomId,
            AccountId = accountId,
            JoinedAt = timeProvider.GetUtcNow(),
        });

        await context.SaveChangesAsync(cancellationToken);
        return RoomMutationResult.Applied();
    }

    /// <summary>
    /// Removes an account's membership of a room.
    /// </summary>
    /// <remarks>
    /// Leaving does not touch the member's messages: a room's history is a shared record, not a
    /// per-member one. Leaving a room the account is not in changes nothing and is not an error.
    /// </remarks>
    public async Task<RoomMutationResult> LeaveAsync(
        Guid roomId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var existing = await context.RoomMemberships.FirstOrDefaultAsync(
            membership => membership.RoomId == roomId && membership.AccountId == accountId,
            cancellationToken);

        if (existing is null)
        {
            return RoomMutationResult.NoChange();
        }

        context.RoomMemberships.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);

        return RoomMutationResult.Applied();
    }

    /// <summary>
    /// Stores a message posted by a member of the room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Membership is checked here, against the database.</b> A caller has to be in the room to
    /// post in it, and that must not be decided by whichever client is asking — a client showing a
    /// room it has left, or one lying outright, both arrive here the same way.
    /// </para>
    /// <para>
    /// <b>Authorship and the timestamp are the server's.</b> Neither is a value a caller may
    /// choose: the author is the authenticated account the caller already proved (§4.7), and the
    /// time is this server's clock. Both are shown to every member of the room, which is exactly why
    /// neither may be supplied by the member who posted.
    /// </para>
    /// </remarks>
    public async Task<(RoomMutationResult Result, RoomMessage? Message)> PostMessageAsync(
        Guid roomId,
        Guid authorAccountId,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormaliseBody(body, out var normalised))
        {
            return (RoomMutationResult.Refused(RoomMutationStatus.InvalidBody), null);
        }

        if (!await context.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken))
        {
            return (RoomMutationResult.Refused(RoomMutationStatus.NotFound), null);
        }

        var isMember = await context.RoomMemberships.AnyAsync(
            membership => membership.RoomId == roomId && membership.AccountId == authorAccountId,
            cancellationToken);

        if (!isMember)
        {
            return (RoomMutationResult.Refused(RoomMutationStatus.NotAMember), null);
        }

        var message = new RoomMessage
        {
            RoomId = roomId,
            AuthorAccountId = authorAccountId,
            PostedAt = timeProvider.GetUtcNow(),
            Body = normalised,
        };

        context.RoomMessages.Add(message);
        await context.SaveChangesAsync(cancellationToken);

        return (RoomMutationResult.Applied(), message);
    }

    /// <summary>
    /// Reads a page of a room's history, newest first.
    /// </summary>
    /// <param name="roomId">The room to read.</param>
    /// <param name="before">
    /// Return messages older than this id. <see langword="null"/> starts at the newest message.
    /// </param>
    /// <param name="pageSize">How many to return, clamped to <see cref="MaxPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// <para>
    /// <b>Membership is deliberately not required.</b> §5.4 says a member can scroll history from
    /// before they joined, which means joining cannot be what unlocks a room's past. History is
    /// readable to this server's members; the caller being one of those is established by the
    /// authenticated connection, not by this room's membership rows.
    /// </para>
    /// <para>
    /// Reads without tracking: nothing here is going to be modified, and tracking a page of history
    /// on every scroll is a cost with no purpose.
    /// </para>
    /// </remarks>
    public async Task<MessagePage> GetHistoryAsync(
        Guid roomId,
        long? before = null,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = context.RoomMessages
            .AsNoTracking()
            .Where(message => message.RoomId == roomId);

        if (before is not null)
        {
            query = query.Where(message => message.Id < before.Value);
        }

        // One more than asked for, so whether a further page exists is known without a second query
        // and without inferring it from a short page.
        var messages = await query
            .OrderByDescending(message => message.Id)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var hasMore = messages.Count > size;
        if (hasMore)
        {
            messages.RemoveAt(messages.Count - 1);
        }

        var nextBefore = hasMore && messages.Count > 0
            ? messages[^1].Id
            : (long?)null;

        return new MessagePage(messages, nextBefore);
    }

    /// <summary>The rooms this account has joined, in the order a channel list should show them.</summary>
    public async Task<IReadOnlyList<Room>> GetJoinedRoomsAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
        => await context.RoomMemberships
            .AsNoTracking()
            .Where(membership => membership.AccountId == accountId)
            .Select(membership => membership.Room!)
            .OrderBy(room => room.Name)
            .ToListAsync(cancellationToken);

    /// <summary>Every room on this server.</summary>
    public async Task<IReadOnlyList<Room>> GetRoomsAsync(CancellationToken cancellationToken = default)
        => await context.Rooms
            .AsNoTracking()
            .OrderBy(room => room.Name)
            .ToListAsync(cancellationToken);

    /// <summary>Whether an account is a member of a room.</summary>
    public async Task<bool> IsMemberAsync(
        Guid roomId,
        Guid accountId,
        CancellationToken cancellationToken = default)
        => await context.RoomMemberships.AnyAsync(
            membership => membership.RoomId == roomId && membership.AccountId == accountId,
            cancellationToken);

    /// <summary>
    /// Trims a room name and rejects it if it is empty or too long.
    /// </summary>
    /// <remarks>
    /// Trimmed rather than rejected for surrounding whitespace, so " general " and "general" cannot
    /// both exist and read identically in a channel list.
    /// </remarks>
    private static bool TryNormaliseName(string name, out string normalised)
    {
        normalised = (name ?? string.Empty).Trim();

        return normalised.Length > 0 && normalised.Length <= Room.NameMaxLength;
    }

    /// <summary>Trims a topic and rejects it if it is too long. Empty is allowed.</summary>
    private static bool TryNormaliseTopic(string? topic, out string normalised)
    {
        normalised = (topic ?? string.Empty).Trim();

        return normalised.Length <= Room.TopicMaxLength;
    }

    /// <summary>
    /// Trims a message body and rejects it if it is empty or too long.
    /// </summary>
    /// <remarks>
    /// Surrounding whitespace is trimmed but the body is otherwise stored exactly as typed — the
    /// server does not rewrite what a member said, and Markdown is the client's business (§9.4).
    /// </remarks>
    private static bool TryNormaliseBody(string body, out string normalised)
    {
        normalised = (body ?? string.Empty).Trim();

        return normalised.Length > 0 && normalised.Length <= RoomMessage.BodyMaxLength;
    }
}
