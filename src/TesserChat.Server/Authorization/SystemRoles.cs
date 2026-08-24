using System.Security.Cryptography;
using System.Text;

namespace TesserChat.Server.Authorization;

/// <summary>
/// The three roles every server ships with (§5.3), and the permissions they start out holding.
/// </summary>
/// <remarks>
/// <para>
/// These are seeded rows, not an enum. Once a server is running they are ordinary roles: an
/// administrator can rename Admin, change what it grants, or ignore it entirely in favour of roles
/// of their own. Only two things are special about them — a system role cannot be deleted, and the
/// Owner cannot be stripped of its power (see <c>Role.IsOwner</c>).
/// </para>
/// <para>
/// The permission sets below are a <i>starting point</i> chosen so a fresh server is usable, not a
/// policy the server enforces afterwards. Nothing re-applies them.
/// </para>
/// </remarks>
internal static class SystemRoles
{
    /// <summary>
    /// Holds every permission implicitly and cannot be deleted or left unassigned (§5.3, §5.6).
    /// </summary>
    public const string OwnerName = "Owner";

    /// <summary>Server administration, short of the Owner's absolute authority.</summary>
    public const string AdminName = "Admin";

    /// <summary>What an ordinary member holds. Grants no administrative permission.</summary>
    public const string MemberName = "Member";

    /// <summary>Stable id of the Owner role.</summary>
    public static Guid OwnerId { get; } = DeriveId(OwnerName);

    /// <summary>Stable id of the Admin role.</summary>
    public static Guid AdminId { get; } = DeriveId(AdminName);

    /// <summary>Stable id of the Member role.</summary>
    public static Guid MemberId { get; } = DeriveId(MemberName);

    /// <summary>
    /// The permissions Admin is seeded with — everything except server-level settings, which stay
    /// with the Owner until an operator decides otherwise.
    /// </summary>
    public static IReadOnlyList<Permission> AdminPermissions { get; } =
    [
        Permission.MembersKick,
        Permission.MembersBan,
        Permission.RolesAssign,
        Permission.MessagesDelete,
        Permission.AuditLogRead,
    ];

    /// <summary>
    /// The permissions Member is seeded with: none.
    /// </summary>
    /// <remarks>
    /// Posting and reading are not modelled as permissions — they are what membership already
    /// means (§5.1). Only moderation and administration need gating, so an ordinary member holds an
    /// empty set rather than a set of implied basics.
    /// </remarks>
    public static IReadOnlyList<Permission> MemberPermissions { get; } = [];

    /// <summary>
    /// Derives a role's id from its seed name, so the value is identical in every deployment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF's <c>HasData</c> needs a key known at migration-generation time; a random GUID would
    /// differ between the migration and every rebuild of it, and <c>Guid.NewGuid()</c> in a seed is
    /// a well-known way to produce a migration that never stops changing.
    /// </para>
    /// <para>
    /// Deriving from the name also means every deployment agrees on these ids, which keeps support
    /// and debugging legible: the Owner role has the same id on every TesserChat server. The name
    /// used here is the <i>seed</i> name and is not affected by a later rename, since the id is
    /// fixed at seed time.
    /// </para>
    /// </remarks>
    private static Guid DeriveId(string seedName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"tesserchat:system-role:v1:{seedName}"));

        Span<byte> bytes = stackalloc byte[16];
        digest.AsSpan(0, 16).CopyTo(bytes);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8, matching AccountId
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        return new Guid(bytes, bigEndian: true);
    }
}
