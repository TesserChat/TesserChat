using TesserChat.Shared.Identity;

namespace TesserChat.Shared.Auth;

/// <summary>
/// The exact bytes a client signs to prove it holds an identity's private key (§4.7).
/// </summary>
/// <remarks>
/// <para>
/// This lives in <c>Shared</c> because client and server must produce byte-identical payloads, and
/// the only way to guarantee that is for both to call the same method. Two implementations of "the
/// same" format drift the moment one side adds a field, and the symptom is every login failing
/// with a signature error that names nothing.
/// </para>
/// <para>
/// <b>Frozen wire format.</b> Changing the layout or <see cref="Context"/> invalidates every
/// in-flight challenge and, more importantly, means a client and server on different builds can no
/// longer authenticate each other at all. A change here is a breaking protocol change and must bump
/// <see cref="ProtocolVersion.MinimumSupported"/> in the same commit.
/// </para>
/// </remarks>
public static class LoginChallenge
{
    /// <summary>Length in bytes of the random nonce the server issues.</summary>
    /// <remarks>
    /// 32 bytes, so collisions between independently issued nonces are not a thing that happens.
    /// The single-use record makes a repeat unusable regardless; this makes it unreachable.
    /// </remarks>
    public const int NonceSize = 32;

    /// <summary>Total length in bytes of the payload that gets signed.</summary>
    /// <remarks>
    /// Derived from the pieces rather than written out, so editing <see cref="Context"/>
    /// cannot leave a hand-counted length behind to silently mislay the layout.
    /// </remarks>
    public const int PayloadSize = ContextLength + ServerIdLength + NonceSize;

    /// <summary>
    /// Domain separator, so a TesserChat login signature cannot be mistaken for — or produced by —
    /// a signature over anything else this key signs.
    /// </summary>
    private static ReadOnlySpan<byte> Context => "tesserchat:login:v1"u8;

    /// <summary>
    /// Length of <see cref="Context"/>. A constant because <c>stackalloc</c> needs one; a test
    /// pins it against the string itself, so editing one without the other fails the build's
    /// test run rather than silently shifting every field after it.
    /// </summary>
    private const int ContextLength = 19;

    private const int ServerIdLength = 16;

    /// <summary>
    /// Writes the payload for <paramref name="nonce"/> on the server identified by
    /// <paramref name="serverId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The server id is what makes a signature unreplayable.</b> Without it, a malicious server
    /// could collect a client's signature over a nonce and present it to a different server to log
    /// in as that user. Binding the target into the signed bytes means a signature produced for one
    /// server verifies nowhere else — that property is the reason this flow exists, not a detail of
    /// it.
    /// </para>
    /// <para>
    /// The server's UUID rather than its hostname: the UUID is written once at setup and never
    /// changes, whereas a hostname changes behind a reverse proxy, on a port change, and whenever a
    /// self-hoster moves domains — each of which would otherwise break every existing client's
    /// login with a signature error.
    /// </para>
    /// <para>
    /// All three fields are fixed width, so they are concatenated with no delimiter and no length
    /// prefix: there is no parse ambiguity to exploit because there is no parsing.
    /// </para>
    /// </remarks>
    /// <param name="serverId">The <c>ServerInstance</c> id of the server being logged in to.</param>
    /// <param name="nonce">The <see cref="NonceSize"/>-byte challenge that server issued.</param>
    /// <param name="destination">A span of at least <see cref="PayloadSize"/> bytes.</param>
    /// <exception cref="ArgumentException">The nonce or the destination is the wrong size.</exception>
    public static void WritePayload(Guid serverId, ReadOnlySpan<byte> nonce, Span<byte> destination)
    {
        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException(
                $"A login nonce must be {NonceSize} bytes, got {nonce.Length}.",
                nameof(nonce));
        }

        if (destination.Length < PayloadSize)
        {
            throw new ArgumentException(
                $"The payload needs {PayloadSize} bytes, got {destination.Length}.",
                nameof(destination));
        }

        Context.CopyTo(destination);

        // Big-endian, so the bytes match the UUID's canonical text form on every platform. Guid's
        // default layout is little-endian for its first three fields, which would make the signed
        // payload differ between architectures if it were ever used on a big-endian one.
        if (!serverId.TryWriteBytes(destination[ContextLength..], bigEndian: true, out _))
        {
            throw new ArgumentException("Could not write the server id.", nameof(serverId));
        }

        nonce.CopyTo(destination[(ContextLength + ServerIdLength)..]);
    }

    /// <summary>
    /// Builds the payload for <paramref name="nonce"/> as a new array.
    /// </summary>
    /// <remarks>
    /// The allocating convenience overload, for the client's signing call and for tests. Prefer
    /// <see cref="WritePayload"/> with a stack buffer on the server's verification path, which runs
    /// per login attempt.
    /// </remarks>
    public static byte[] BuildPayload(Guid serverId, ReadOnlySpan<byte> nonce)
    {
        var payload = new byte[PayloadSize];
        WritePayload(serverId, nonce, payload);
        return payload;
    }

    /// <summary>
    /// Signs the challenge for <paramref name="serverId"/> with <paramref name="identity"/>.
    /// </summary>
    /// <remarks>
    /// The whole client side of §4.7 step 3. Offered here so a client never has to assemble the
    /// payload itself and cannot get the layout subtly wrong.
    /// </remarks>
    public static byte[] Sign(IdentityKeyPair identity, Guid serverId, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(identity);

        Span<byte> payload = stackalloc byte[PayloadSize];
        WritePayload(serverId, nonce, payload);
        return identity.Sign(payload);
    }

    /// <summary>
    /// Verifies a challenge signature against a raw Ed25519 public key.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for a bad signature, the wrong signer, a signature made for
    /// a different server, or malformed input of any kind — everything here arrives over the wire,
    /// so rejection is an expected outcome rather than an exceptional one.
    /// </remarks>
    public static bool Verify(
        ReadOnlySpan<byte> signingPublicKey,
        Guid serverId,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> signature)
    {
        if (nonce.Length != NonceSize)
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[PayloadSize];
        WritePayload(serverId, nonce, payload);
        return IdentityKeyPair.Verify(signingPublicKey, payload, signature);
    }
}
