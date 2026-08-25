using TesserChat.Client.Storage;

namespace TesserChat.Client.Tests.Storage;

/// <summary>
/// Covers the client's local database: servers, sessions, contacts, and DM history (§9.5).
/// </summary>
/// <remarks>
/// Against a real SQLite database rather than a fake, for the same reason the server tests use a
/// real Postgres (§5.4): the behaviour worth testing here is the schema's — unique indexes, the
/// cascade, conflict handling — and none of that exists in a stub.
/// </remarks>
public sealed class LocalStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    // --- Servers -------------------------------------------------------------------------------

    [Fact]
    public void AServer_RoundTrips()
    {
        using var store = LocalStore.OpenInMemory();

        var server = new KnownServer(
            Guid.NewGuid(),
            "https://chat.example:5001",
            "Example",
            AccountId: Guid.NewGuid(),
            AddedAt: Now,
            LastConnectedAt: Now.AddMinutes(5));

        store.SaveServer(server);

        var stored = Assert.Single(store.GetServers());
        Assert.Equal(server, stored);
    }

    [Fact]
    public void AServerNeverConnectedTo_RoundTripsWithNulls()
    {
        using var store = LocalStore.OpenInMemory();

        // A server can be saved before it has ever been logged into, so both of these are null.
        var server = new KnownServer(
            Guid.NewGuid(),
            "https://chat.example:5001",
            "Example",
            AccountId: null,
            AddedAt: Now,
            LastConnectedAt: null);

        store.SaveServer(server);

        var stored = Assert.Single(store.GetServers());
        Assert.Null(stored.AccountId);
        Assert.Null(stored.LastConnectedAt);
    }

    [Fact]
    public void SavingAServerTwice_UpdatesItRatherThanDuplicating()
    {
        using var store = LocalStore.OpenInMemory();
        var id = Guid.NewGuid();

        store.SaveServer(new KnownServer(id, "https://a.example", "Old", null, Now, null));
        store.SaveServer(new KnownServer(id, "https://a.example", "New", null, Now, null));

        var stored = Assert.Single(store.GetServers());
        Assert.Equal("New", stored.Name);
    }

    [Fact]
    public void TwoServersAtOneAddress_AreRefused()
    {
        using var store = LocalStore.OpenInMemory();

        store.SaveServer(new KnownServer(
            Guid.NewGuid(), "https://a.example", "First", null, Now, null));

        // Enforced by a unique index rather than a check in code: the same server reached by one
        // address must not appear twice in the server rail.
        Assert.ThrowsAny<Exception>(() => store.SaveServer(new KnownServer(
            Guid.NewGuid(), "https://a.example", "Second", null, Now, null)));
    }

    [Fact]
    public void Servers_AreOrderedByMostRecentlyConnected()
    {
        using var store = LocalStore.OpenInMemory();

        var never = Guid.NewGuid();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        store.SaveServer(new KnownServer(never, "https://c.example", "Never", null, Now, null));
        store.SaveServer(new KnownServer(
            older, "https://a.example", "Older", null, Now, Now.AddHours(-2)));
        store.SaveServer(new KnownServer(
            newer, "https://b.example", "Newer", null, Now, Now.AddMinutes(-1)));

        // The order §7.2 wants for defaulting a DM's route, with never-connected last rather than
        // first.
        Assert.Equal([newer, older, never], store.GetServers().Select(server => server.Id));
    }

    [Fact]
    public void MarkConnected_UpdatesOnlyThatServer()
    {
        using var store = LocalStore.OpenInMemory();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.SaveServer(new KnownServer(first, "https://a.example", "A", null, Now, null));
        store.SaveServer(new KnownServer(second, "https://b.example", "B", null, Now, null));

        store.MarkConnected(first, Now.AddMinutes(10));

        var servers = store.GetServers().ToDictionary(server => server.Id);
        Assert.Equal(Now.AddMinutes(10), servers[first].LastConnectedAt);
        Assert.Null(servers[second].LastConnectedAt);
    }

    // --- Session tokens ------------------------------------------------------------------------

    [Fact]
    public void ASession_RoundTrips()
    {
        using var store = LocalStore.OpenInMemory();
        var serverId = SaveAServer(store);

        var session = new CachedSession(serverId, "a.b.c", Guid.NewGuid(), Now.AddHours(12));
        store.SaveSession(session);

        Assert.Equal(session, store.GetSession(serverId, Now));
    }

    [Fact]
    public void AnExpiredSession_ReadsAsAbsent()
    {
        using var store = LocalStore.OpenInMemory();
        var serverId = SaveAServer(store);

        store.SaveSession(new CachedSession(
            serverId, "a.b.c", Guid.NewGuid(), Now.AddHours(-1)));

        // Reported absent rather than returned for the caller to check: both cases have the same
        // remedy, and a caller that forgot would send a token certain to be rejected (§4.7.6).
        Assert.Null(store.GetSession(serverId, Now));
    }

    [Fact]
    public void ASessionExpiringExactlyNow_ReadsAsAbsent()
    {
        using var store = LocalStore.OpenInMemory();
        var serverId = SaveAServer(store);

        store.SaveSession(new CachedSession(serverId, "a.b.c", Guid.NewGuid(), Now));

        Assert.Null(store.GetSession(serverId, Now));
    }

    [Fact]
    public void SavingASession_ReplacesThePreviousOne()
    {
        using var store = LocalStore.OpenInMemory();
        var serverId = SaveAServer(store);
        var accountId = Guid.NewGuid();

        store.SaveSession(new CachedSession(serverId, "old", accountId, Now.AddHours(1)));
        store.SaveSession(new CachedSession(serverId, "new", accountId, Now.AddHours(12)));

        var stored = store.GetSession(serverId, Now);
        Assert.NotNull(stored);
        Assert.Equal("new", stored.Token);
    }

    [Fact]
    public void ClearingASession_LeavesTheServer()
    {
        using var store = LocalStore.OpenInMemory();
        var serverId = SaveAServer(store);
        store.SaveSession(new CachedSession(serverId, "a.b.c", Guid.NewGuid(), Now.AddHours(12)));

        store.ClearSession(serverId);

        // Signing out forgets the token, not the server — and never the identity key, which is not
        // in this store at all (§4.2).
        Assert.Null(store.GetSession(serverId, Now));
        Assert.Single(store.GetServers());
    }

    [Fact]
    public void RemovingAServer_TakesItsCachedTokenWithIt()
    {
        using var store = LocalStore.OpenInMemory();
        var serverId = SaveAServer(store);
        store.SaveSession(new CachedSession(serverId, "a.b.c", Guid.NewGuid(), Now.AddHours(12)));

        store.RemoveServer(serverId);

        // By cascade, so a caller cannot leave a token authenticating against a server that is gone.
        Assert.Empty(store.GetServers());
        Assert.Null(store.GetSession(serverId, Now));
    }

    // --- Contacts ------------------------------------------------------------------------------

    [Fact]
    public void AContact_RoundTrips()
    {
        using var store = LocalStore.OpenInMemory();

        var contact = new Contact("sign-key", "encrypt-key", "Ada", Now, IsBlocked: false);
        store.SaveContact(contact);

        Assert.Equal(contact, store.GetContact("sign-key"));
        Assert.Equal(contact, Assert.Single(store.GetContacts()));
    }

    [Fact]
    public void AnUnknownContact_ReadsAsNull()
    {
        using var store = LocalStore.OpenInMemory();

        Assert.Null(store.GetContact("never-seen"));
    }

    [Fact]
    public void SavingAContactTwice_UpdatesItRatherThanDuplicating()
    {
        using var store = LocalStore.OpenInMemory();

        store.SaveContact(new Contact("sign-key", "encrypt-key", "Ada", Now, false));
        store.SaveContact(new Contact("sign-key", "encrypt-key", "Ada Lovelace", Now, false));

        var stored = Assert.Single(store.GetContacts());
        Assert.Equal("Ada Lovelace", stored.DisplayName);
    }

    [Fact]
    public void ABlockedContact_ReadsAsBlocked()
    {
        using var store = LocalStore.OpenInMemory();

        store.SaveContact(new Contact("sign-key", "encrypt-key", "Mallory", Now, IsBlocked: true));

        Assert.True(store.IsBlocked("sign-key"));
    }

    [Fact]
    public void AnUnknownKey_IsNotBlocked()
    {
        using var store = LocalStore.OpenInMemory();

        // Not blocked, same as an explicitly unblocked contact. §7.5.2 sends an unknown sender to
        // the first-contact prompt, which the caller reaches by asking whether the contact exists.
        Assert.False(store.IsBlocked("never-seen"));
        Assert.Null(store.GetContact("never-seen"));
    }

    [Fact]
    public void Unblocking_IsSavingTheContactUnblocked()
    {
        using var store = LocalStore.OpenInMemory();

        store.SaveContact(new Contact("sign-key", "encrypt-key", "Mallory", Now, true));
        store.SaveContact(new Contact("sign-key", "encrypt-key", "Mallory", Now, false));

        Assert.False(store.IsBlocked("sign-key"));
    }

    [Fact]
    public void RemovingAContact_KeepsTheConversation()
    {
        using var store = LocalStore.OpenInMemory();
        store.SaveContact(new Contact("peer", "encrypt-key", "Ada", Now, false));
        store.TryAddMessage(NewMessage("peer", "m1", "Still here."));

        store.RemoveContact("peer");

        // Forgetting who someone is must not silently delete the conversation with them.
        Assert.Null(store.GetContact("peer"));
        Assert.Single(store.GetThread("peer"));
    }

    // --- Direct messages -----------------------------------------------------------------------

    [Fact]
    public void AMessage_RoundTrips()
    {
        using var store = LocalStore.OpenInMemory();

        var added = store.TryAddMessage(NewMessage("peer", "m1", "Hello."));

        Assert.True(added);

        var stored = Assert.Single(store.GetThread("peer"));
        Assert.Equal("m1", stored.MessageId);
        Assert.Equal("Hello.", stored.Body);
        Assert.Equal("peer", stored.PeerKey);
    }

    [Fact]
    public void TheSameMessageTwice_IsStoredOnce()
    {
        using var store = LocalStore.OpenInMemory();

        var first = store.TryAddMessage(NewMessage("peer", "m1", "Hello."));
        var second = store.TryAddMessage(NewMessage("peer", "m1", "Hello."));

        // §7.4's dedup case: the same message fanned out through several servers arrives more than
        // once and must be shown once. The caller still acks the duplicate.
        Assert.True(first);
        Assert.False(second);
        Assert.Single(store.GetThread("peer"));
    }

    [Fact]
    public void TwoMessagesSentInTheSameInstant_AreBothStored()
    {
        using var store = LocalStore.OpenInMemory();

        var at = Now;
        Assert.True(store.TryAddMessage(
            new DirectMessage(0, "peer", "m1", false, at, "First.", null)));
        Assert.True(store.TryAddMessage(
            new DirectMessage(0, "peer", "m2", false, at, "Second.", null)));

        // §7.4 notes two distinct messages can share a millisecond, which is why dedup is on the
        // sender's message id rather than on the timestamp.
        Assert.Equal(2, store.GetThread("peer").Count);
    }

    [Fact]
    public void AThread_ReadsAsOneThreadAcrossDifferentRelayingServers()
    {
        using var store = LocalStore.OpenInMemory();

        var firstServer = Guid.NewGuid();
        var secondServer = Guid.NewGuid();

        store.TryAddMessage(new DirectMessage(
            0, "peer", "m1", false, Now, "Through one server.", firstServer));
        store.TryAddMessage(new DirectMessage(
            0, "peer", "m2", true, Now.AddMinutes(1), "Through another.", secondServer));

        // §7.3: history is keyed by peer, not by which server relayed it, so a pair moving to a
        // different shared server keeps one continuous conversation.
        var thread = store.GetThread("peer");

        Assert.Equal(2, thread.Count);
        Assert.Equal(["Through one server.", "Through another."], thread.Select(m => m.Body));
        Assert.Equal([firstServer, secondServer], thread.Select(m => m.ReceivedVia));
    }

    [Fact]
    public void AThread_HoldsOnlyItsOwnPeersMessages()
    {
        using var store = LocalStore.OpenInMemory();

        store.TryAddMessage(NewMessage("ada", "m1", "To Ada."));
        store.TryAddMessage(NewMessage("grace", "m2", "To Grace."));

        Assert.Equal("To Ada.", Assert.Single(store.GetThread("ada")).Body);
        Assert.Equal("To Grace.", Assert.Single(store.GetThread("grace")).Body);
    }

    [Fact]
    public void AThread_IsOrderedOldestFirst()
    {
        using var store = LocalStore.OpenInMemory();

        store.TryAddMessage(NewMessage("peer", "m1", "First."));
        store.TryAddMessage(NewMessage("peer", "m2", "Second."));
        store.TryAddMessage(NewMessage("peer", "m3", "Third."));

        Assert.Equal(
            ["First.", "Second.", "Third."],
            store.GetThread("peer").Select(message => message.Body));
    }

    [Fact]
    public void BothDirections_AreStoredInOneThread()
    {
        using var store = LocalStore.OpenInMemory();

        store.TryAddMessage(new DirectMessage(0, "peer", "m1", false, Now, "Theirs.", null));
        store.TryAddMessage(
            new DirectMessage(0, "peer", "m2", true, Now.AddMinutes(1), "Mine.", null));

        var thread = store.GetThread("peer");

        Assert.Equal(2, thread.Count);
        Assert.False(thread[0].SentByMe);
        Assert.True(thread[1].SentByMe);
    }

    [Fact]
    public void ThreadPeers_AreMostRecentlyActiveFirst()
    {
        using var store = LocalStore.OpenInMemory();

        store.TryAddMessage(NewMessage("ada", "m1", "Older."));
        store.TryAddMessage(NewMessage("grace", "m2", "Newer."));

        Assert.Equal(["grace", "ada"], store.GetThreadPeers());
    }

    [Fact]
    public void DeletingAThread_LeavesOtherThreadsAlone()
    {
        using var store = LocalStore.OpenInMemory();

        store.TryAddMessage(NewMessage("ada", "m1", "To Ada."));
        store.TryAddMessage(NewMessage("grace", "m2", "To Grace."));

        store.DeleteThread("ada");

        Assert.Empty(store.GetThread("ada"));
        Assert.Single(store.GetThread("grace"));
    }

    private static Guid SaveAServer(LocalStore store)
    {
        var id = Guid.NewGuid();
        store.SaveServer(new KnownServer(id, "https://a.example", "A", null, Now, null));

        return id;
    }

    private static DirectMessage NewMessage(string peer, string messageId, string body)
        => new(0, peer, messageId, SentByMe: false, Now, body, ReceivedVia: null);
}
