using TesserChat.Server.Persistence;
using TesserChat.Shared.Rooms;

namespace TesserChat.Server.Rooms;

/// <summary>
/// Turns room entities into the contracts sent over the wire (§5.4.1).
/// </summary>
/// <remarks>
/// A deliberate boundary rather than sending entities directly. An entity carries navigation
/// properties and whatever columns the schema happens to have; serialising one would put the
/// server's storage shape on the wire, so a column added for the server's own use would silently
/// become part of the protocol.
/// </remarks>
internal static class RoomMappings
{
    /// <summary>Describes a room to a client.</summary>
    public static RoomSummary ToSummary(this Room room)
    {
        ArgumentNullException.ThrowIfNull(room);

        return new RoomSummary(room.Id, room.Name, room.Topic);
    }

    /// <summary>Describes a message to a client.</summary>
    /// <remarks>
    /// The timestamp is formatted round-trippable and UTC-normalised, so a client parsing it gets
    /// the instant the server meant regardless of either machine's time zone.
    /// </remarks>
    public static RoomMessageDto ToDto(this RoomMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new RoomMessageDto(
            message.Id,
            message.RoomId,
            message.AuthorAccountId,
            message.PostedAt.ToUniversalTime().ToString("O"),
            message.Body);
    }

    /// <summary>Describes a page of history to a client.</summary>
    public static MessagePageDto ToDto(this MessagePage page)
        => new([.. page.Messages.Select(message => message.ToDto())], page.NextBefore);
}
