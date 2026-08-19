using TesserChat.Shared;

namespace TesserChat.Shared.Tests;

public class ProtocolVersionTests
{
    [Fact]
    public void MinimumSupported_IsNotAheadOfCurrent()
    {
        // A build that refuses its own protocol version would reject every peer, including itself.
        Assert.True(ProtocolVersion.MinimumSupported <= ProtocolVersion.Current);
    }

    [Fact]
    public void IsSupported_AcceptsOwnVersion()
    {
        Assert.True(ProtocolVersion.IsSupported(ProtocolVersion.Current));
    }

    [Fact]
    public void IsSupported_RejectsVersionBelowMinimum()
    {
        Assert.False(ProtocolVersion.IsSupported(ProtocolVersion.MinimumSupported - 1));
    }

    [Fact]
    public void IsSupported_RejectsNewerPeer()
    {
        // A self-hosted server may well be running a build newer than this client.
        Assert.False(ProtocolVersion.IsSupported(ProtocolVersion.Current + 1));
    }
}
