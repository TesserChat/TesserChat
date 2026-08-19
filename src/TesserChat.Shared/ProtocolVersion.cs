namespace TesserChat.Shared;

/// <summary>
/// Version of the TesserChat wire protocol this build speaks.
/// </summary>
/// <remarks>
/// Because every server is self-hosted and updated independently, a client will
/// routinely meet servers running older or newer builds. Clients and servers exchange this value
/// on connect so a mismatch surfaces as a clear error rather than a malformed-payload failure.
/// </remarks>
public static class ProtocolVersion
{
    /// <summary>The protocol version this build implements.</summary>
    public const int Current = 1;

    /// <summary>
    /// The oldest protocol version this build can still talk to. Bumped only on a breaking
    /// wire-format change, which drops support for peers below it.
    /// </summary>
    public const int MinimumSupported = 1;

    /// <summary>
    /// Whether this build can communicate with a peer advertising <paramref name="peerVersion"/>.
    /// </summary>
    public static bool IsSupported(int peerVersion)
        => peerVersion >= MinimumSupported && peerVersion <= Current;
}
