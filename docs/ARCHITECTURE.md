# TesserChat — Architecture & Design Decisions

This document is the source of truth for TesserChat's architecture and design decisions. It exists
so development doesn't re-litigate settled questions, and so a contributor can find out *why*
something is the way it is without reconstructing it from the code. If a decision here needs to
change, update this document in the same pull request as the code change.

Sections are numbered and stable — issues, pull requests, and code comments reference them by
number (e.g. "§4.1"), so avoid renumbering when editing.

## 0. Development Workflow

These process rules apply to every feature area in this document and take priority over speed of
implementation.

### 0.1 Tests Are Written Alongside Each Feature, Not After
- Framework: **xUnit** — settled and scaffolded; all three test projects are on xUnit 2.9.x with
  `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, and `coverlet.collector`. Each test project
  has `<Using Include="Xunit" />` so `using Xunit;` is implicit in test files.
- Test project layout mirrors `/src` and already exists: `/tests/TesserChat.Server.Tests`,
  `/tests/TesserChat.Client.Tests`, `/tests/TesserChat.Shared.Tests` — each referencing exactly its
  one counterpart under `/src`.
- `TesserChat.Server` declares `InternalsVisibleTo("TesserChat.Server.Tests")`, so server internals
  can be tested directly without widening their accessibility for real consumers. The server test
  project also carries `Microsoft.AspNetCore.Mvc.Testing`, and `Program.cs` ends with
  `public partial class Program;` specifically so tests can boot the real host through
  `WebApplicationFactory<Program>` rather than re-registering services by hand.
- A feature is not "done" without tests covering its core logic. This applies most strictly to:
  - `TesserChat.Shared` crypto helpers (signing, verification, ECDH, AEAD encrypt/decrypt) — the
    highest-stakes code in the project. Cover negative cases explicitly: wrong key, tampered
    ciphertext, expired/reused nonce — not just the happy path.
  - Server-side auth flow (challenge-response issuance/verification, JWT validation).
  - Role/permission resolution (§5.3) — since the system is dynamic from day one, tests should
    cover permission resolution generally, not just behavior of the 3 default roles.
  - Offline mailbox dedup/ack/TTL logic (§7.4) — this has real edge cases (duplicate timestamps,
    partial ack across multiple queuing servers) that look correct until they aren't.
  - The recipient membership check (§7.4.1) — cover the rejection path explicitly, not just the
    accept path: a non-member recipient is discarded rather than queued, a membership revoked
    between enqueue and delivery stops delivery, and the rejection is not distinguishable to the
    sender from a successful queue.
  - Client-side blocking and first contact (§7.5) — the ordering is the thing to test: a forged
    sender field fails to decrypt and never reaches the block check or the UI; a blocked sender's
    message is dropped after decryption but still acked; an unknown sender produces a prompt
    carrying no message content; and a known sender bypasses the prompt entirely.
- Avalonia ViewModels (MVVM) are unit-testable independent of the view — test those. Full UI/visual
  testing is lower priority for v1 (Avalonia has a headless test mode if that becomes worth the
  investment later; don't block on it now).
- Tests run in CI (GitHub Actions) on every PR. A PR should not be mergeable with failing tests, or
  with no tests covering the code it introduces.

### 0.2 One Feature Per Pull Request
- Stop and open a PR at feature-sized boundaries. Don't keep stacking unrelated work onto an
  unmerged branch.
- "Feature-sized" roughly maps to the subsections in this document — e.g. identity generation
  (§4.2), challenge-response login (§4.7), and presence subscription (§8.2) are each their own PR,
  not bundled into one large "auth" PR.
- If a feature turns out bigger than expected mid-implementation, that's a signal to split it
  further, not a reason to push through — several small, reviewable PRs beat one large one.
- Each PR includes its tests (per §0.1) and a short description of what changed and how it was
  verified.
- Treat each numbered feature area as a natural stopping point — implement it, write its tests,
  open the pull request, and pause there rather than continuing straight on to the next feature.

### 0.3 CI Enforcement
- Workflow: `.github/workflows/ci.yml`, job `build-and-test`, named `Build & Test (${{ matrix.os }})`
  in the GitHub UI — that is the name to select when enabling required status checks. It runs
  `dotnet restore` / `build --configuration Release --no-restore` /
  `test --configuration Release --no-build` against `TesserChat.slnx` on every PR and on push to
  `main`. Matrix across `ubuntu-latest`, `windows-latest`, and `macos-latest` with
  `fail-fast: false`, so one platform failing still reports the other two — the client has real
  per-OS code paths (secure storage in particular, §4.2) worth catching before merge, not after.
  Free to run this way — GitHub Actions has no minute limit on standard runners for a public repo.
- Because the build is `--no-restore` / `--no-build` at each later step, a project missing from
  `TesserChat.slnx` is silently skipped by CI rather than failing loudly. See §3.
- **The workflow file alone does not block merging.** That's a separate, one-time repo setting:
  `Settings → Branches → branch protection rule on main → enable "Require status checks to pass
  before merging" → select the CI job`. This isn't expressible as a committed file — it has to be
  turned on manually once the repo exists on GitHub, ideally before the first outside contributor
  shows up.
- Postgres-backed integration tests use **Testcontainers**, not a `services:` block, and run on
  **`ubuntu-latest` only**. They need a Linux container: the Windows runner's daemon serves Windows
  containers and the macOS runner has no daemon at all, so neither can run them — and a
  `services:` block would not have helped, since those attach to Linux runners too. Testcontainers
  at least means the same code path works on a developer machine with Docker in Linux-container
  mode.
- Where Docker can't serve them those tests **skip**; the Test step sets
  `TESSERCHAT_REQUIRE_DOCKER` on the Linux runner to turn skipping off there. Without that, a
  broken or missing daemon on the one runner that matters would report green having tested
  nothing — a silent skip is the failure mode this arrangement has to guard against.

### 0.4 What Is Actually Built Today

The solution builds clean (Release, 0 warnings under `TreatWarningsAsErrors`) with **252 tests
passing** across the three test projects. The server runs in Docker and manages accounts, roles,
admission, its own first-run setup, and the challenge-response half of login. **None of it is
reachable by a client**: there are no endpoints beyond `/health`, nothing issues a session token,
and there is no chat transport, so every service below is exercised only in-process and by tests.

| Piece | State |
|---|---|
| Solution, six projects, `Directory.Build.props`, `global.json`, `.gitattributes` | done |
| CI workflow, 3-OS matrix, branch protection ruleset | done (§0.3) |
| `GET /health` → `{ "status": "ok" }`, unauthenticated liveness probe | done |
| EF Core + Npgsql, migrations, migrate-on-startup, Testcontainers harness (§5.4) | done |
| `IdentityKeyPair`, `AccountId`, `PublicIdentity` (§4.1, §5.1) | done |
| Account registration and public key storage (§5.1) | done |
| Dynamic roles, permissions, and resolution (§5.3) | done |
| Connection modes: open, password-gated, allowlist (§5.2) | done |
| First-run setup and Owner assignment (§5.6) | done |
| Docker image and compose pairing (§5.6) | done |
| Audit log for role changes and setup (§5.5) | done — kicks, bans, deletions still owed (§5.5.4) |
| Challenge-response login: signed payload, nonce issue/consume (§4.7) | done — no endpoint, no token yet |
| `TesserChat.Shared.ProtocolVersion` (`Current`/`MinimumSupported`/`IsSupported`) | done, not yet wired to anything |
| Avalonia client booting a Dark-variant Fluent theme with a bound `MainWindowViewModel` | placeholder shell |
| Everything else in this document | not started |

**Nothing built so far is reachable from outside the process.** There are no HTTP or SignalR
endpoints beyond `/health`, so there is no way for a client to register, log in, or manage roles.
`AccountRegistrar`, `PermissionResolver`, `RoleManager`, and `ChallengeAuthenticator` are internal
classes exercised by tests. `ChallengeAuthenticator` can authenticate an identity today but nothing
calls it over the wire and nothing turns its result into a session token — that is #13, then the
transport work in §6. Note that neither "role management endpoints" nor "role management UI" is
currently tracked as an issue.

`MainWindowViewModel` currently exposes only `Title` and a placeholder `Greeting` — it is a wiring
proof for the MVVM split, not a design for the real window (§9.2).

`ProtocolVersion` is still referenced by neither client nor server; wiring the version exchange into
the connect handshake is part of the client login work (#14). It now has company: the login payload
(§4.7.1) is real wire format too, and **`MinimumSupported` must be bumped in the same PR as any
breaking change to either**.

## 1. What TesserChat Is

A self-hosted, Discord-style chat platform. Anyone can run a server on their own hardware. There is
**no central service, no central account system, and no central directory** — a user's identity is
a public/private keypair they control, portable across every server they join.

Guiding principle for every design decision below: **the server is a dumb, self-hosted relay for a
specific community. The client owns identity, contacts, DM history, and cross-server state.**

## 2. Tech Stack

**Status column**: *in* = the dependency is referenced by a project today; *planned* = decided, but
nothing depends on it yet. Only the *in* rows are load-bearing for a build right now.

| Layer | Choice | Status |
|---|---|---|
| Target framework | **`net10.0`**, set once in `Directory.Build.props` for every project | in |
| Server | ASP.NET Core (`Microsoft.NET.Sdk.Web`), SignalR for real-time | in / SignalR planned |
| Server persistence | PostgreSQL via **EF Core** + Npgsql, snake_case naming (§5.4) | in |
| Client | Avalonia UI **12.1.x**, MVVM via **CommunityToolkit.Mvvm** 8.4.x | in |
| Client local storage | OS-native secure storage (private keys) + SQLite/LiteDB (everything else local) | planned |
| Crypto | NSec (libsodium binding) — Ed25519 signing, X25519 ECDH, XChaCha20-Poly1305 AEAD | planned |
| Voice (future) | SIPSorcery (WebRTC/RTP/ICE/DTLS-SRTP in pure C#) | planned |
| Client packaging/updates | Velopack — per-OS installers + auto-update, GitHub Releases as source | planned |
| Server deployment | Docker image, primary distribution path | in |
| Config | `appsettings.json` (gitignored; `appsettings.example.json` is the tracked template) | in |
| Testing | xUnit 2.9.x + `Microsoft.NET.Test.Sdk`, `coverlet.collector`, Testcontainers for Postgres | in |
| License | MIT (`LICENSE` at repo root) | in |

### 2.1 Solution-Wide Build Settings

`Directory.Build.props` at the repo root is the single place these are set — do not repeat them in
individual `.csproj` files:

- `TargetFramework` = `net10.0`, `LangVersion` = `latest`
- `Nullable` = `enable`, `ImplicitUsings` = `enable`
- `TreatWarningsAsErrors` = `true`, `EnableNETAnalyzers` = `true`, `AnalysisLevel` = `latest`
- Package metadata: `Authors`, `Product`, `PackageLicenseExpression` (MIT), `RepositoryUrl`

**`TreatWarningsAsErrors` is on solution-wide.** A new analyzer warning fails the build, including
in CI — fix the cause rather than suppressing it, and if a suppression is genuinely correct, make it
narrow and comment why.

`global.json` pins the SDK to `10.0.400` with `rollForward: latestFeature`, so local and CI builds
agree on SDK behavior. Bumping the TFM means touching `Directory.Build.props`, `global.json`, and
the `dotnet-version` in `.github/workflows/ci.yml` together.

**A .NET 10 SDK (≥ 10.0.400) is required to build at all.** `rollForward: latestFeature` will not
roll *back* from a 9.x SDK — `dotnet build` fails outright with "A compatible .NET SDK was not
found" rather than falling back. The `.slnx` solution format also needs the .NET 10 SDK. If you hit
that error, install the SDK (`winget install Microsoft.DotNet.SDK.10` on Windows); do not "fix" it
by loosening `global.json`. Installing side-by-side with an older SDK is fine — `global.json`
selects 10.x for this repo regardless of what else is present.

## 3. Repo Structure (monorepo)

Six projects — three under `/src`, one mirrored test project each under `/tests`.

```
/TesserChat
  /src
    /TesserChat.Server           # ASP.NET Core host (Microsoft.NET.Sdk.Web)
      Program.cs                 #   minimal-host entry point; /health endpoint
      appsettings.example.json   #   tracked template — real appsettings.json is gitignored
      /Persistence               #   EF Core: DbContext, entities, options (§5.4)
        /Migrations              #     scaffolded by `dotnet ef`; hand-edited only for SQL
                                 #     EF cannot express (§5.5.1), with a comment saying why
      /Accounts                  #   registration, public key storage, admission (§5.1, §5.2)
      /Setup                     #   first-run setup and Owner assignment (§5.6)
      /Auditing                  #   append-only moderation/admin log (§5.5)
      /Auth                      #   challenge issue/verify, nonce lifecycle (§4.7)
      /Authorization             #   roles, permission catalogue, resolution (§5.3)
      /Properties
        launchSettings.json
    /TesserChat.Client           # Avalonia desktop app (WinExe), MVVM
      Program.cs                 #   Avalonia AppBuilder entry point
      App.axaml(.cs)             #   Application, Fluent theme, Dark variant
      app.manifest
      /Views                     #   MainWindow.axaml(.cs)
      /ViewModels                #   ViewModelBase, MainWindowViewModel
    /TesserChat.Shared           # Wire-protocol DTOs + crypto helpers, no dependencies
      ProtocolVersion.cs
      /Auth                      #   LoginChallenge — the signed login payload (§4.7.1)
      /Identity                  #   IdentityKeyPair, AccountId, PublicIdentity (§4.1)
  /tests
    /TesserChat.Server.Tests     # HealthEndpointTests.cs
    /TesserChat.Client.Tests     # MainWindowViewModelTests.cs
    /TesserChat.Shared.Tests     # ProtocolVersionTests.cs
  /.github/workflows/ci.yml
  /.config/dotnet-tools.json     # pinned local tools (dotnet-ef) — `dotnet tool restore`
  Dockerfile                     # server image (§5.6)
  compose.yaml                   # server + Postgres — the self-hosting path
  .env.example                   # tracked template for compose config; .env is gitignored
  .dockerignore
  Directory.Build.props          # solution-wide TFM + analyzer settings (§2.1)
  global.json                    # SDK pin
  .gitattributes                 # line-ending normalization across the 3 CI platforms
  TesserChat.slnx
  README.md
  LICENSE
```

**The solution file is `TesserChat.slnx`, not `TesserChat.sln`** — the XML solution format that is
the .NET 10 SDK default. Every `dotnet` command targets it by name (`dotnet build TesserChat.slnx`),
as CI does. A new project must be added to the `<Folder Name="/src/">` or `<Folder Name="/tests/">`
element inside it, or it will not build in CI even though it builds locally in an IDE.

There is **no `/docs` directory yet** — create it when there is a document that belongs there rather
than as an empty placeholder.

### 3.1 Project Reference Rules

`Shared` exists specifically so the challenge-response payloads, DM envelope format, and any
DTOs used over SignalR aren't duplicated (and drifted) between client and server.

- `Server → Shared` and `Client → Shared`. **`Server` and `Client` never reference each other**, and
  **`Shared` references neither** — it must stay free of ASP.NET Core and Avalonia types so both
  sides can depend on it without dragging in the other's stack.
- Each test project references exactly its one counterpart under `/src`.

### 3.2 Local Configuration

`.gitignore` excludes `appsettings.json` and `appsettings.*.json` (with `appsettings.example.json`
re-included as the tracked template) because real config holds per-deployment secrets. When a
feature introduces a new config key, **add it to `appsettings.example.json`** — that file is the
only record of which keys exist.


## 4. Identity & Auth

### 4.1 Keys
Every identity is **one 32-byte master seed** that expands into **two keypairs**:
- **Ed25519** — signing, used for the login challenge-response and as the account's permanent ID.
  The seed *is* this key's private key.
- **X25519** — encryption, used only for DM key exchange (see §7). Derived from the seed via
  `HKDF-SHA256(ikm: seed, salt: none, info: "tesserchat:x25519-from-ed25519-seed:v1")`.

**Why one seed rather than two independent keys.** The user has to move and safeguard this material
across devices and backups. One secret means one file, one passphrase, and no possibility of a
backup that restores half an identity — the failure mode where a user recovers their login but
silently loses the ability to decrypt their DM history. Two independently generated keys bought a
key-hygiene property that was never real in practice, since both keys always shared one keystore
entry and one backup file anyway.

**The two keys are therefore not independent secrets** — anyone holding the seed can derive the
encryption key. What is retained is the separation that actually matters: two distinct keys used
with two distinct algorithms, so a flaw or misuse in either does not implicate the other.

**This is key derivation, not curve conversion.** HKDF produces 32 deterministic bytes, which
X25519 accepts as a private key because it clamps the scalar internally. The Ed25519→X25519
birational map exists in libsodium but is *not* exposed by NSec, and hand-writing curve arithmetic
in the project's most security-sensitive code would be a poor trade for the 32 bytes it saves.

> **Frozen wire format.** The HKDF info string above must never change. Changing it makes every
> restored identity derive a different encryption key, breaking decryption of all existing direct
> messages — with no error at restore time to signal that it happened. A test pins the derivation
> against a vector computed independently of NSec (RFC 7748) to catch any drift.

### 4.2 New Identity Flow (client-generated — the default path)
1. Client generates a random 32-byte seed and expands it into both keypairs (§4.1).
2. Private key material is written **directly into OS-native secure storage** — Keychain (macOS),
   DPAPI/Credential Manager (Windows), Secret Service/libsecret (Linux). A raw private key file is
   never written to disk as part of this flow.
3. Client shows the user their public key fingerprint for reference/sharing.
4. Client offers an **encrypted backup export** immediately (see §4.5) — without this, losing the
   device loses the identity permanently (no central recovery).

### 4.3 Import Flow (existing key file, drag-and-drop or file picker)
1. User selects an `openssh-key-v1` format private key file and supplies its passphrase.
2. Parse/decrypt using `SSH.NET`'s `PrivateKeyFile` class in isolation (no SSH transport code —
   just the parser).
3. On success: extract key material, write into OS-native secure storage exactly like the generate
   flow, and **discard any reference to the original file** going forward. Import is a one-time
   ingestion event, not an ongoing dependency.
4. Registers as a "known identity" in the client, labeled by the user (e.g. "Work laptop key").

### 4.4 Multi-Device
Deliberately supported via **key file reuse**, not per-device keys: importing the same private key
on a second device gives that device the same login. This is a conscious tradeoff — simple, no
central service needed, consistent with how SSH keys already work — accepted knowing it means no
way to revoke a single compromised device without regenerating the whole identity. Users who want
per-device isolation can simply generate a fresh key per device instead; nothing in the server model
prevents an account from having multiple registered public keys later if this needs revisiting.

### 4.5 Encrypted Key Backup / Export
- Exportable at any time from the client, not just at creation.
- Passphrase-protected (Argon2id-derived key — memory-hard, resists offline brute force far better
  than PBKDF2). Suggested library: `Konscious.Security.Cryptography.Argon2` — verify current
  maintenance status before locking in.
- Format: standard `openssh-key-v1` container so it's also readable by normal SSH tooling if
  the user ever wants that.
- **The container holds the single 32-byte master seed** (§4.1), not two separate keys. That is what
  keeps the format standard — `openssh-key-v1` has no slot for a second key — and what keeps
  device-to-device transfer down to one file and one passphrase. The X25519 key is re-derived on
  import, so a restored identity recovers both its login and its DM history from that one secret.

### 4.6 Local Vault Lock (failed-attempt wipe)
Scope: **this only ever triggers on failed local unlock attempts against the on-device encrypted
vault** (wrong passphrase typed into the client itself). It must never be triggerable by anything
server-side — a malicious server must not be able to force a remote wipe.
- Failed-attempt counter persists across app restarts (killing/reopening the app must not reset it).
- Exponential backoff between attempts (1s, 2s, 4s, 8s...) in addition to the hard cutoff.
- On threshold: **crypto-shred**, don't rely on file overwrite — destroy the local
  data-encryption key that protects the DM log, rather than trying to overwrite the log itself
  (unreliable on SSDs due to wear-leveling). A best-effort file overwrite can run too, as
  defense-in-depth, but the key destruction is what actually guarantees unrecoverability.
- **DM-log wipe threshold and identity-key wipe threshold are separate**, with the identity-key
  one set deliberately higher / harder to hit by accident. Losing chat history to a typo is
  recoverable annoyance; losing your only copy of your identity key is catastrophic and
  irreversible given there's no central recovery path.
- Wrong-passphrase detection is free: the vault is AEAD-encrypted (XChaCha20-Poly1305), so a failed
  authentication-tag check on decrypt *is* the "wrong passphrase" signal — no separate check needed.

### 4.7 Login (Challenge-Response)

A client proves it holds an identity's private key without that key ever leaving the device. Built
as `LoginChallenge` (`TesserChat.Shared`) and `ChallengeAuthenticator` (server).

1. Client requests a challenge from the server it's connecting to.
2. Server returns a **32-byte random nonce**, its own **server id**, and an expiry.
3. Client signs the payload below with its Ed25519 private key.
4. Server consumes the nonce, verifies the signature, and resolves the signing key to an account.
5. Server issues a session token (JWT, #13) authenticating subsequent REST calls and the SignalR
   hub connection (via SignalR's `AccessTokenProvider`, standard ASP.NET Core JWT bearer flow).

Steps 1–4 are built. Step 5 is not: `ChallengeAuthenticator` returns an authenticated account id
and nothing turns that into a token yet. Neither step is reachable over HTTP — there are no auth
endpoints (§0.4).

#### 4.7.1 The Signed Payload Is Shared, Frozen Wire Format

```
"tesserchat:login:v1"  (19 bytes)  domain separator
server id              (16 bytes)  big-endian UUID
nonce                  (32 bytes)  the challenge
```

Concatenated with no delimiter or length prefix — every field is fixed width, so there is no
parsing to get wrong and no ambiguity to exploit.

`LoginChallenge` lives in `TesserChat.Shared` because **client and server must produce
byte-identical payloads**, and the only way to guarantee that is for both to call the same method.
Two implementations of "the same" format drift the moment one side adds a field, and the symptom is
every login failing with a signature error that names nothing.

**Changing this layout or the context string is a breaking protocol change** and must bump
`ProtocolVersion.MinimumSupported` in the same PR. The domain separator is what stops a signature
over a bare nonce from also being a valid login signature, so anything that ever gets an identity
to sign attacker-chosen bytes is not thereby a login oracle.

#### 4.7.2 Scoped to the Server UUID, Not the Hostname

The server's `ServerInstance.Id` (§5.6) goes into the signed payload — **not** its hostname, despite
this section previously saying "server ID/hostname".

This is the security property the whole flow exists for. Without it, a malicious server could
collect a client's signature and replay it against a *different* server to log in as that user.
Binding the target into the signed bytes means a signature produced for one server verifies nowhere
else.

The UUID rather than the hostname because the UUID is written once at setup and never changes,
whereas a hostname changes behind a reverse proxy, on a port change, and whenever a self-hoster
moves domains. Binding to a hostname would break every existing client's login on each of those,
with a signature error that points at nothing.

The tradeoff, stated plainly: **a client learns the server's UUID from the same server it is
authenticating to**, so on first contact it cannot independently verify it reached the server it
meant to. That is trust-on-first-use, closed later by pinning the id client-side (#14) — and a
hostname would not have closed it either, since the client learns which host it dialled from its own
configuration but learns nothing about who answers.

**An unconfigured server cannot authenticate anyone**, because it has no id to bind to. It issues no
challenges and refuses every login. The way into a fresh server is setup (§5.6), not login.

#### 4.7.3 Single Use Is Enforced by Postgres, Not by the Code Above It

Challenges live in a `login_nonces` table, keyed by the nonce value itself. Consuming one is a
**single conditional UPDATE** — set `consumed_at` where the value matches, is unconsumed, and has
not expired:

- Postgres serialises the matching rows, so of two requests presenting the same nonce at the same
  instant, **exactly one sees a row affected**. A read-then-write would let both pass their check
  before either wrote — and would pass every test that did not run the two concurrently.
- Expiry is part of that same statement rather than checked around it, so there is no window in
  which an expiring nonce is spent by one path and rejected by another.
- It survives a restart, and it still holds if a self-hoster ever runs two instances against one
  database. An in-process cache would give the same answers right up until either happened.

**The nonce is consumed before the signature is verified.** Spending it first means a wrong
signature burns the challenge rather than leaving it available to retry — an attacker gets one
attempt per nonce, not unlimited attempts against a nonce that stays valid until it expires. A
legitimate client that fumbles asks for another, costing it one round trip.

Spent rows are kept, not deleted: a consumed row is what makes a replay distinguishable from a nonce
that never existed. A sweep (`Auth:ChallengeRetention`, default 15 minutes past expiry) clears them
eventually. The sweep is housekeeping, not correctness — an unswept nonce is still unusable, because
consuming one checks expiry itself.

**The nonce table is not an audit record.** Challenges are issued to anyone who asks, before any
identity is proven, so the table says only that a challenge was handed out — never who logged in.
Authenticated actions belong in the audit log (§5.5).

#### 4.7.4 Requesting a Challenge Reveals Nothing

A caller does not say who they are to get a nonce. Challenge issuance therefore **cannot be used to
test whether a public key is registered here**, which is the same enumeration boundary §5.2 draws
around admission and §8.2 draws around presence. Who signed is established from the signature, at
verification.

The same reasoning applies to failures. Internally `LoginStatus` distinguishes an expired challenge,
a replayed one, a bad signature, and an unregistered key — those distinctions are for the server's
own logs. **The endpoint that eventually surfaces this (#13) must answer every failed login with one
undifferentiated rejection.** Telling a stranger their nonce expired rather than that their key is
unknown turns login into an oracle for which public keys are registered here.

The account lookup runs last, after the signature verifies, so an unregistered key and a bad
signature do the same work in the same order and the refusal reveals nothing by its timing.

#### 4.7.5 Configuration

| Key | Default | Meaning |
|---|---|---|
| `Auth:ChallengeLifetime` | `00:02:00` | How long a client has to sign a challenge |
| `Auth:ChallengeRetention` | `00:15:00` | How long spent/expired challenges are kept before sweeping |

Two minutes is long enough for a slow link and a device waking up, short enough that a captured
challenge is worthless by the time it could be used. Signing is a local operation taking
microseconds, so this is almost entirely network and interface latency — it is configurable for the
self-hoster on a genuinely bad link, not because the default needs tuning.

Retention is deliberately non-zero so a replay arriving just after expiry still meets a row and is
refused as spent, rather than looking like a nonce that never existed.

## 5. Server

### 5.1 Public Key = Identity
- A hash of the public key becomes the account's permanent UUID on that server.
- Display name is cosmetic and freely changeable; the UUID is the true identifier used everywhere
  internally (permissions, audit trail, message authorship).
- Server stores the mapping UUID → public key → display name.

As built (`src/TesserChat.Server/Accounts/`, `Persistence/Account.cs`):

```
accounts (id, signing_key UNIQUE, encryption_key, display_name, registered_at)
```

**The id is derived, not generated** — `AccountId.FromPublicKey` over the Ed25519 key (§4.1), so
registration is idempotent by construction: the same key presented twice resolves to the same
account, and survives a database restore unchanged. The unique index on `signing_key` states the
same rule independently of the id scheme.

**Both public keys are stored on one row.** The signing key is what logins verify against; the
X25519 key is published so a DM partner can discover an encryption key without a directory service
(§7.1). Pairing them at the storage layer removes any way to match the two wrongly later.

**Display names are not unique and are never identifiers.** Two members may pick the same name;
they stay distinct accounts. Re-registering an existing key is *not* a rename — a returning member
keeps the name they last set.

Concurrent registrations of one key resolve to a single account: the attempt that loses the insert
race re-reads and reports the winner's row.

### 5.2 Server Connection Modes
Configurable per server instance, at setup time (changeable later by an account holding
`server.manage`, §5.3):
1. **Open** — anyone can connect and self-register a public key. The default.
2. **Password-gated initial connect** — a shared password required only for the first
   registration; subsequent logins use the normal challenge-response.
3. **Allowlist-only** — only pre-approved public keys can register at all.

Configured under `Connection` in `appsettings.json`, or as `Connection__Mode` etc. in a container
(§3.2). Read through `IOptionsMonitor`, so a mode change takes effect on the next registration
rather than at the next restart.

#### 5.2.1 The gate applies to joining, never to logging in

A joining credential is **first-contact only**. The gate is consulted only for a key the server has
not seen; a key it already knows is returning, not joining, and authenticates by challenge-response
(§4.7) instead.

This ordering is load-bearing. Re-gating a known key would lock out every existing member the
moment an operator rotated the password or trimmed the allowlist — turning routine maintenance into
an outage. It also means removing a key from the allowlist does **not** remove an account that
already registered; that is kicking or banning (§5.5).

#### 5.2.2 Admission is a set of policies, not a switch

Each mode is an `IAdmissionPolicy` selected per registration, rather than a `switch` at the call
site. That is deliberate: **single-use invite links (#44) are a fourth admission path, not a fourth
mode** — an invite admits a key the configured mode would otherwise refuse. Building the gate as a
policy set means invites arrive as an added implementation rather than a rewrite of the gate and
every caller.

`AdmissionRequest` carries the credentials a caller presents, with a field for an invite token
already in the shape. Nothing reads it yet.

#### 5.2.3 Refusals are uniform, and misconfiguration fails closed

**A refusal carries no reason.** A caller cannot tell a wrong password from an unlisted key, because
every distinguishable reason tells an unauthenticated stranger something about a server they have
not joined: which mode it runs, or — by probing keys — what is on its allowlist. That is the same
boundary §7.4.1 and §8.2 draw elsewhere. The operator knows their own mode and tells invitees out of
band; nobody who needs the distinction has to learn it from a rejection.

**Misconfiguration refuses everyone rather than admitting them.** A password-gated server with no
password hash configured, an empty allowlist, and a mode with no registered policy all admit nobody.
The opposite default silently turns a restricted server open, which hands it to whoever notices
first.

#### 5.2.4 The joining password is stored hashed

`Connection:JoinSecretHash` holds a salted PBKDF2-SHA256 hash, never the password. An operator
reading their own database — or a leaked backup — does not thereby learn a secret they can hand to
someone else, or one a member may have reused elsewhere. `dotnet run -- hash-join-secret "<password>"`
produces the value.

**PBKDF2 here, Argon2id for key backup (§4.5)** — different problems. A backup file is stolen
outright and attacked offline forever, so memory-hardness is what protects an identity. A joining
password is server-side, gates one action, and can be rotated the moment it leaks. Revisiting this
if Argon2 arrives for §4.5 is cheap: only the stored format changes, and only for servers using this
mode.

Since one password is shared by everyone, it cannot be revoked for one person and it leaks the first
time someone forwards it — which is the gap invites (#44) exist to close.

### 5.3 Roles & Permissions
Built as a **dynamic system from day one** — roles are not an enum. Roles map to a permission set
many-to-many, so an administrator can create roles and choose what each grants at runtime, with no
restart and no schema migration.
- Ship with 3 default roles: **Owner, Admin, Member.**
- **Owner** is assigned automatically to whoever completes the first-run setup wizard (see §5.6)
  and is non-deletable/non-demotable (a server needs at least one Owner at all times).
- Roles and permissions are **per-server** — there is no global role system, consistent with there
  being no global account system.

As built (`src/TesserChat.Server/Authorization/`, `Persistence/Role.cs`, `PermissionGrant.cs`):

```
roles            (id, name UNIQUE, is_system_role, is_owner, created_at)
permissions      (key PK, description)            -- e.g. "messages.delete", "members.kick"
role_permissions (role_id, permission_key)
account_roles    (account_id, role_id, granted_at)
```

**No `server_id`.** One deployment is one server with one database, the same scoping accounts use
(§5.1) — a column that always held the single `server_instances` id would buy nothing. Adding it is
a straightforward migration if one database ever hosts several servers.

**`account_roles`, not `user_roles`** — "account" is the term the rest of the server uses.

#### 5.3.1 Roles are data; permission keys are code

The dynamic half is roles: create them, name them, choose their permissions, assign them to members,
all at runtime. The **set of permission keys is fixed in code** (`Authorization/Permission.cs`) and
seeded into `permissions` by migration.

This is deliberate, and it is the one place the system is not dynamic. A permission key means
something only where the server checks it, so a key an administrator could invent would appear in
the role editor and grant nothing — a bug wearing flexibility's clothes. Adding a permission is a
code change made in the same PR as the enforcement point that gives it meaning.

Seeding the catalogue into the database is what lets a client render a role editor without shipping
its own copy of the list.

Keys are dotted `area.action` strings and are **stable forever once shipped** — they are stored in
`role_permissions`, so renaming one silently strips that permission from every role holding it.

#### 5.3.2 The Owner's authority is a flag, not rows

The Owner role is seeded with **no `role_permissions` rows at all**. It holds every permission
implicitly, decided during resolution (`PermissionResolver`), specifically so there is nothing an
administrator can uncheck to reduce it.

Authority comes from the `is_owner` flag, never from the role's name. Renaming the Owner is allowed
and costs it nothing — a server that calls its Owner "Founder" still has an Owner. Nothing in the
server may branch on a role's name.

At most one Owner *role* exists, held by a partial unique index on `is_owner`, so the rule survives
a code path that bypasses the mutation layer.

#### 5.3.3 Where the invariants are enforced

In the **mutation layer** (`RoleManager`), not in the UI — a client is free to be wrong about what
it offers.

- **A server keeps at least one account holding Owner.** Checked before revoking an Owner
  assignment. This binds *every* caller, an Owner acting on themselves included: a server with no
  Owner has nobody able to appoint one, so it is not a permission anyone can hold.
- **System roles cannot be deleted.** A server with no Member role has nothing to give a new member.
- **The Owner role's permission set cannot be edited** (see §5.3.2). Non-Owner system roles *can*
  be — an operator may legitimately want a more or less powerful Admin.
- **Unknown permission keys are refused outright**, not silently dropped, so a typo cannot quietly
  produce a role granting less than the caller believes.

`RoleManager` deliberately does **not** check whether the caller is permitted to act. That is
`PermissionResolver`'s job at the endpoint. Mixing the two would make the invariants above depend on
the caller's permissions, which is exactly what they must not do.

#### 5.3.4 Resolution

Effective permissions are the **union** of what every role an account holds grants, deduplicated —
a permission granted by two roles resolves once. An account with no roles, and an account that does
not exist, both resolve to no permissions: absence is denial, never an error, since a caller gating
an action has nothing to do with the difference.

`PermissionResolver` is scoped and caches nothing, so a role change takes effect on the next
resolution without a restart. Note that a long-lived scope — a SignalR connection holding one open
(§6) — resolves against the state its `DbContext` first saw.

### 5.4 Persistence (PostgreSQL)
- **ORM: EF Core** with Npgsql. Chosen as the path of least resistance in .NET — migrations are
  built in, which matters for a self-hosted product where operators upgrade their own servers and
  a schema change has to apply cleanly to someone else's live database.
- **Connection string: `ConnectionStrings:Postgres`**, overridable in a container as the
  `ConnectionStrings__Postgres` environment variable. The server refuses to start without it and
  names the key in the error, rather than booting and failing on the first query.
- **Migrations are applied on startup**, controlled by `Database:MigrateOnStartup` (default
  `true`). The target operator upgrades by pulling a new Docker image (§5.6), and should not have
  to exec into a container and run tooling to finish the job; a server quietly serving requests
  against last version's schema is the worse failure. An operator running a change-controlled
  database sets this to `false` and applies migrations themselves, and then a server started
  against an out-of-date schema fails on first use rather than at boot.
- Migrations live in `src/TesserChat.Server/Persistence/Migrations` and are scaffolded with the
  version-pinned local tool — `dotnet tool restore` once, then
  `dotnet ef migrations add <Name> --project src/TesserChat.Server --output-dir Persistence/Migrations`.
  An `IDesignTimeDbContextFactory` keeps the tooling from booting the real host to find the
  context, so scaffolding never reaches for a developer's `appsettings.json` or the database it
  names.
- **Tables and columns are snake_case**, applied by convention (`EFCore.NamingConventions`) rather
  than per-property attributes. Postgres folds unquoted identifiers to lowercase, so EF's default
  PascalCase would have to be double-quoted in every query an operator ever hand-writes against
  their own database. Changing this later is a rename migration across every table, so it is
  settled here.
- The schema today, one table per concern:

  | Table | Holds | Section |
  |---|---|---|
  | `server_instances` | this deployment's own id and name; at most one row | §5.6.3 |
  | `accounts` | registered public keys, display names | §5.1 |
  | `roles`, `permissions`, `role_permissions`, `account_roles` | the dynamic role system | §5.3 |
  | `audit_entries` | moderation and administration actions; append-only | §5.5 |
  | `login_nonces` | outstanding and spent login challenges; transient | §4.7.3 |

  `login_nonces` is the only transient table so far. It is swept on a retention window rather than
  kept, and it is deliberately **not** an audit record — challenges are issued before any identity
  is proven, so the table says nothing about who logged in.
- Integration tests run against a **real Postgres**, not an in-memory provider: the in-memory
  provider does not enforce constraints or model Postgres behaviour, so it would pass on schemas
  Postgres rejects. Testcontainers, Linux-only in CI — see §0.3 for why, and for the guard against
  those tests silently skipping.
- Room messages: persisted permanently (so members can scroll history from before they joined).
- File/image attachments: in scope for v1 — store blobs on disk (or S3-compatible object storage
  later) with metadata rows in Postgres; don't put binary blobs directly in Postgres.
- DM mailbox queue: see §7.4 — a *separate*, transient table, not the same as room message storage.

### 5.5 Audit Log
All moderation/admin actions tied to the acting UUID (§5.1), with a timestamp and the affected
target. Built as `src/TesserChat.Server/Auditing/`, over an `audit_entries` table.

#### 5.5.1 Append-only, enforced by the database

Postgres rules reject UPDATE and DELETE on `audit_entries`:

```sql
CREATE RULE audit_entries_no_update AS ON UPDATE TO audit_entries DO INSTEAD NOTHING;
CREATE RULE audit_entries_no_delete AS ON DELETE TO audit_entries DO INSTEAD NOTHING;
```

**In the database rather than by convention**, because an audit log a moderator can quietly edit is
not an audit log — and the code that must not rewrite history is exactly the code most likely to
grow a path that does. This holds against a future call site that forgets, and against anyone with a
`psql` connection.

Note the rules make such a write a **silent no-op**, not an error: a rule rewrites the statement, it
does not raise. That is the wanted behaviour — a stray write changes nothing.

Retention pruning, should it ever be wanted, means a migration that deliberately drops these rules
and recreates them afterwards. That friction is the point: deleting an audit trail should be
something someone writes down, not a `DELETE` someone runs.

#### 5.5.2 The account ids are not foreign keys

Every other join in this schema cascades on account deletion. `audit_entries` deliberately does not:
if deleting an account erased the record of what it did, deleting an account would be the obvious
way to cover tracks. The actor and target ids are stored as plain values, so the trail survives.

An id whose account is gone stops resolving to a display name, which is exactly what a deleted
account should look like. For the same reason `detail` carries a **denormalised human-readable
summary** — "Granted role 'Moderator'" still means something after the Moderator role is deleted,
whereas a dangling role id does not.

`detail` must never carry a secret: it is readable by anyone holding `auditlog.read` (§5.3).

#### 5.5.3 Recording is part of the action

Entries are added to the caller's `DbContext` and saved by the caller's transaction, so a committed
change always has its entry and a rolled-back one leaves none. An audit log written afterwards on a
best-effort basis is missing exactly the entries that matter most.

Consequently a **refused or no-op mutation records nothing** — granting a role an account already
holds, or a revocation refused because it would remove the last Owner, did not happen and must not
appear as though they had.

#### 5.5.4 What is recorded today

This section names role changes, kicks/bans, and message deletions as the floor. **Only role changes
exist to be logged.** Kicks and bans are not built — `members.kick` and `members.ban` are defined
and resolve, but nothing enforces them (#48). Message deletion is M3 (#17).

Recorded now:

| Action | Actor |
|---|---|
| `ServerSetUp` | none — setup precedes any account (§5.6) |
| `RoleCreated`, `RoleRenamed`, `RoleDeleted` | the acting account |
| `RolePermissionsChanged` | the acting account |
| `RoleGranted`, `RoleRevoked` | the acting account |

Still owed by the floor: kicks, bans, message deletions. Each arrives with the feature that makes it
possible, in the same change as the call site that records it.

A null actor means **"the server did this"**, never "we do not know who did this".

`AuditAction` is stored as its member name, not an integer, so a row read directly in `psql` says
`RoleGranted` rather than `3`. Member names are therefore frozen once shipped — renaming one
reinterprets every existing row.

### 5.6 Setup & Deployment
- Primary distribution: **Docker image** (`docker pull` as the target onboarding step).
- On first boot with no existing config, the server enters a **setup mode**: generates/prompts for
  an initial admin password and basic server config (name, connection mode, etc.).
- The first client to complete setup through this flow registers its public key and is
  automatically assigned the **Owner** role.
- Config lives in `appsettings.json`, standard ASP.NET Core configuration layering (env var
  overrides for container deployments). That file is gitignored; `appsettings.example.json` is the
  tracked template and must be updated whenever a new key is introduced (§3.2).
- Both are built: the image and its Compose pairing (§5.6.0), and setup itself
  (`src/TesserChat.Server/Setup/`). Setup is not yet reachable over HTTP — there is no endpoint for
  a client to complete it through, only the service the tests drive (§0.4).

#### 5.6.1 Setup is unauthenticated, so it runs exactly once

There is no Owner to authorize setup — the account that will hold Owner does not exist until setup
creates it. Three properties make that safe, and each is enforced rather than assumed:

- **It runs once.** "Setup is complete" is defined as the `server_instances` row existing, which the
  completing transaction writes. An already-configured server refuses outright, so setup can never
  become an unauthenticated route to Owner on a live server. This holds across restarts, and binds
  the original Owner too — setup is a one-time event, not an administrative action.
- **It is atomic.** The Owner's account, the Owner role grant, and the server row are written in one
  transaction. A crash partway leaves the server unconfigured and retryable rather than half-owned.
- **At most one server row can exist**, held by a unique index on a constant column rather than by
  whichever request checked first. Concurrent claimants therefore resolve to one Owner: one wins,
  the rest are told the server is already configured.

#### 5.6.2 Pinning who may claim ownership

`Setup:OwnerPublicKey` pins the public key permitted to complete setup. With it set, **setup stops
being a race and becomes a claim exactly one key can make** — so a fresh server can be published to
a public address safely. It takes a bare Ed25519 key or a full shareable identity token, whichever
the operator has to hand, and is compared in constant time.

**Left unset, the first client to complete setup becomes Owner.** That is the right default for
bringing a server up on a machine nothing else can reach, and the wrong one for a container
published straight to the internet, where whoever scans the port first takes the server. Because the
unsafe case is invisible until it has already happened, an unconfigured server with no key pinned
**logs a warning on every boot** naming the setting to set.

A key that is set but unreadable refuses everyone: an operator who pinned a key meant to restrict
setup, and a value that will not parse is not evidence they changed their mind.

#### 5.6.3 What the server row records

`server_instances` holds the deployment's stable id (which the login nonce scoping in §4.7 binds
signatures to), its name, when setup completed, and the account that completed it.

`set_up_by_account_id` is for the audit trail (§5.5) — it records who set the server up, which stays
true even after they hand the Owner role on. **It is not how ownership is resolved**; that is the
Owner role (§5.3), which can be granted to others or moved.

## 6. Real-Time Transport

SignalR hub(s) over the authenticated connection from §4.7. Two responsibilities on the same
connection:
- **Room chat** — plaintext to the server (rooms are not E2E encrypted, by design — see §7).
- **Presence** — see §8.2, implemented via SignalR **Groups** (`presence:{pubkey}`), not custom
  fan-out bookkeeping.

## 7. Direct Messages (1:1, E2E Encrypted)

Rooms are intentionally **not** E2E encrypted (open community chat). DMs are, always.

### 7.1 Encryption
- Each user's X25519 public key (from §4.1) is published alongside their Ed25519 identity key when
  they join a server — this is how a DM partner's encryption key gets discovered (no separate
  directory service).
- Shared secret via X25519 ECDH, symmetric encryption via XChaCha20-Poly1305 (`crypto_box`-style,
  one call via NSec).
- Static per-person keys, not per-conversation — cache the derived shared secret client-side after
  first computation.
- **No forward secrecy in v1** (accepted tradeoff — a proper Double Ratchet has no mature .NET
  library and would need to be hand-built against the published spec; revisit post-v1 if it
  becomes a priority).
- The envelope carries the **claimed sender X25519 public key** in cleartext alongside the
  ciphertext. The recipient needs it to know which shared secret to derive — with static per-person
  keys there is no session to look it up from. It is a *claim*, not a credential: it is trusted only
  once decryption succeeds, since the AEAD tag verifies only for the real holder of that key
  (§7.5.1). The server sees this field, so it learns that two keys are corresponding even though it
  cannot read what they say.

### 7.2 Routing
- A DM can only be sent while the two users share at least one server (that server carries the
  bytes — there's no other transport for it).
- If online on multiple shared servers, default to most-recently-active, with manual override in
  the UI. The message composer UI always shows which server is routing the current conversation.

### 7.3 History
**Entirely client-side.** The server never stores DM plaintext or ciphertext long-term — see §7.4
for the one deliberate exception (the transient offline queue). DM threads are keyed by the other
person's public key, not by which server relayed them, so a conversation reads as one continuous
thread even if the pair later moves to messaging through a different shared server.

### 7.4 Offline Mailbox (queued delivery)
When the recipient isn't currently reachable:
1. Sender's client fans the encrypted message out to **every server it shares with the
   recipient** — servers where the recipient is also a member, not every server the sender happens
   to be connected to. Queuing on a server the recipient will never visit achieves nothing.
2. **Each receiving server independently verifies that the recipient UUID is a member of that
   server, and discards the message outright if not** (see §7.4.1). The sender's fan-out targeting
   is a client-side intention; this check is the server-side enforcement of it.
3. Each receiving server stores the ciphertext blob in a transient queue table, keyed by recipient
   UUID, **Unix-timestamped**.
4. When a server observes the recipient's next heartbeat/connect, it delivers any queued
   message(s) for them.
5. Recipient's client dedupes: a message with a timestamp it's already seen/displayed is discarded
   rather than shown twice (since the same message may have been queued on several shared servers).
6. Client acknowledges receipt back to whichever server delivered it; that server clears its queue
   entry for that message.

**Queue-handling notes worth building in from the start, not just discovered later:**
- The client should **ack even messages it discards as duplicates** — otherwise the *other* servers
  that also queued a copy never learn it's safe to clear their queue, and it sits there forever.
- Add a **TTL-based garbage-collection backstop** on the queue table regardless (e.g. purge queued
  entries after N days) — covers the case where the recipient never reconnects to some of the
  fanned-out servers again.
- Consider a client-generated message GUID alongside the Unix timestamp for dedup, not the
  timestamp alone — two distinct messages sent in the same millisecond is a real (if rare) edge
  case the timestamp-only scheme doesn't handle. Optional hardening, not blocking v1.

#### 7.4.1 Recipient Membership Check (server-side, mandatory)

Before a server accepts a relayed or queued DM, it resolves the destination public key to a local
account and confirms that account is a **member of this server**. If it is not, the message is
**discarded immediately** — not queued, not held pending a future join.

This matters because the fan-out in step 1 is decided entirely by the *sender's* client, from the
sender's own view of which servers it shares with the recipient. That view can be stale, wrong, or
deliberately falsified. Without this check, any client could push arbitrary ciphertext blobs into
the queue table of any server it can reach, addressed to users who will never collect them —
unbounded write access to someone else's storage, from an unprivileged account. The check turns the
queue into something a server only holds on behalf of its own members.

Notes on the check itself:

- **Discard silently; do not report back whether the recipient is a member.** A per-server "is this
  pubkey here?" oracle would turn every server into a membership-enumeration endpoint for arbitrary
  keys, which is exactly the boundary §8.2 draws around presence. The sender's client already knows
  which servers it believes are shared and does not need the confirmation.
- **Discarding is not an error the sender needs to act on.** The message is fanned out to every
  shared server precisely so that any one of them can deliver it; a server correctly dropping a
  message it should never have received is normal operation, not a failure. Do not surface it to
  the user, and do not let it fail the send.
- **Check at enqueue, and again at delivery.** Membership can be revoked between the two — someone
  is kicked, or leaves — and a message queued while they were a member should not be delivered
  afterwards. Re-checking at delivery also purges entries stranded by a membership change.
- **This check is about membership, not blocking.** Blocking is client-side (§7.5.1) — the server
  cannot evaluate a block list because it cannot tell who an encrypted DM is from. Should
  server-side enforcement ever land (backlog, §7.5.3), this is the pipeline position for it.
- **The check is on the recipient, not the sender.** A server relays for members; it does not
  require the *sender* to be a member of that server. Whether it should is a separate question tied
  to abuse handling (§7.5) — flagged, not settled here.

### 7.5 Blocking, First Contact & Abuse

Friend-add requires no mutual approval (§8.1), so the corresponding responsibility falls on
**blocking and on how unknown senders are surfaced**. Both are **client-side** decisions.

#### 7.5.1 Blocking is client-side

The server cannot evaluate a block list, because it cannot tell who a DM is from. The payload is
end-to-end encrypted (§7.1) and the server holds no key that opens it. Only the recipient's client
can establish the sender's identity, so only the recipient's client can act on one.

On receiving a DM, the client:

1. Reads the **claimed sender public key** from the envelope and derives the shared secret for it.
2. **Decrypts.** Success is what authenticates the claim — an XChaCha20-Poly1305 tag only verifies
   if the message really was sealed by the holder of that private key, so a forged sender field
   fails here rather than being believed. A failed decrypt is discarded and never shown.
3. **Checks the now-authenticated sender against the block list**, and discards the message if
   blocked — before it reaches the UI, notifications, or unread counts.

Order matters: the block check comes *after* decryption, never before. Trusting the envelope's
unauthenticated sender field would let anyone bypass a block by putting someone else's key in it,
and would equally let them forge a message as a trusted contact.

A blocked sender's message is **dropped silently**. No delivery failure, no "you have been blocked"
signal, no read receipt — blocking should not tell the blocked party they were blocked. The client
still **acks the message to the relaying server** exactly as it would a duplicate (§7.4), so the
server clears its queue entry rather than retaining and retrying it forever.

#### 7.5.2 First contact from an unknown sender

A message from a public key the user has never corresponded with is **not shown as an ordinary
message**. Delivering unsolicited content straight into the conversation list is what makes
unsolicited-message abuse effective, and the no-approval friend model (§8.1) means anyone who
learns a public key can send.

Instead the client surfaces a **first-contact prompt** identifying the sender by fingerprint (§4.2)
and offering exactly three actions:

- **View** — open the message and treat this pubkey as known from now on, so subsequent messages
  arrive normally without re-prompting.
- **Discard** — drop this message. The sender stays unknown, so a later message prompts again.
- **Block** — discard and add to the block list, applying §7.5.1 to everything from that key
  thereafter.

Constraints on the prompt, since it is itself an abuse surface:

- **Show the message only after "View".** The prompt itself must not render message content, or it
  becomes the delivery vector it exists to prevent. Metadata only: the sender's fingerprint, and
  optionally which server relayed it.
- **Collapse repeat prompts per sender.** Many pending messages from one unknown key are one
  prompt, not one per message, or the prompt queue becomes the abuse channel.
- **Decrypt before prompting.** The sender is only known once decryption succeeds (§7.5.1), so
  first-contact classification happens after that step, not on the envelope's claim.
- **Prompting is not a delivery failure.** Ack to the relaying server as normal, so its queue
  clears whichever action the user eventually takes.
- Deciding a pubkey is "known" is local state. A user who has an existing DM thread with someone,
  or has added them as a friend (§8.1), is past first contact and is never prompted for them.

#### 7.5.3 Storage abuse

The §7.4.1 recipient membership check already bounds who can write into a server's queue — only
messages addressed to that server's own members are stored at all. Blocking narrows what a
recipient *sees* but not what a server *holds*, since the server still cannot identify senders.

Whether servers should additionally enforce block lists — which would require exposing sender
identity to the server and giving up some of the privacy §7.1 buys — remains **backlog**, and is
now a weaker case than before: membership-scoped queues plus the TTL backstop (§7.4) bound
retention without it.

## 8. Friends & Presence

### 8.1 Friends
- Add-by-pubkey. **No mutual approval required** — this is "save this contact," not a request
  workflow. The added user can block if they don't want to receive messages.
- Client can generate a shareable "add friend" string encoding a pubkey.

### 8.2 Presence
- Push-based, not polled — client subscribes per-pubkey via SignalR group membership
  (`presence:{pubkey}`); server pushes on connect/disconnect to everyone in that group.
- On subscribe, server replies with a **current-state snapshot** (not just future deltas), so the
  friends list isn't empty until the next status change happens to fire.
- Presence lookups are **scoped to servers where the target pubkey is already a member** — this is
  the privacy boundary discussed: it's equivalent to an existing member list being visible to other
  members, not an open enumeration surface.
- Client holds a SignalR connection open to **every saved server simultaneously** while the app is
  running (desktop app, no OS suspension concerns) — same model as Discord's own client. Merge
  per-server presence events into one local `pubkey → { server: status }` view; this feeds both the
  friends-list indicator and the DM-routing-server indicator from the same shared state.
- Per-server reconnect/backoff, isolated — one self-hosted box being down shouldn't affect others.
  Stagger initial connection attempts on app launch rather than firing all at once.

## 9. Client (Avalonia)

### 9.1 Framework
Avalonia UI **12.1.x** — genuinely cross-platform (Windows/macOS/Linux, plus iOS/Android/WebAssembly
if mobile ever comes back into scope), XAML-based, MVVM, renders natively (no embedded
browser/webview — a hard requirement per earlier discussion). Shares the `TesserChat.Shared` project
directly with the server.

As scaffolded:

- Packages: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, and
  `CommunityToolkit.Mvvm`. The project is `WinExe` with `BuiltInComInteropSupport`, an
  `app.manifest`, and `AvaloniaUseCompiledBindingsByDefault` = `true`.
- **Compiled bindings are on by default** — bindings are checked at build time, and since
  `TreatWarningsAsErrors` is on (§2.1), a binding to a property that doesn't exist fails the build.
  Give every `DataTemplate` and view an `x:DataType` rather than reaching for `{Binding}` with
  reflection fallback.
- **MVVM is CommunityToolkit.Mvvm.** `ViewModelBase` derives from its `ObservableObject`; use its
  `[ObservableProperty]` and `[RelayCommand]` source generators rather than hand-writing
  `INotifyPropertyChanged` or a bespoke command type.
- **`ViewModelBase` and everything under `/ViewModels` must not reference Avalonia UI types** — that
  is what keeps view models unit-testable from `TesserChat.Client.Tests` without a UI toolkit, per
  §0.1. Push toolkit-dependent work (dialogs, clipboard, file pickers) behind an interface that the
  view layer implements.

### 9.2 Layout — Discord-esque
- **Server rail** (left edge, icon list) → **channel/DM list** (second column) → **chat pane** →
  optional **member list** (right).
- Voice channels appear in the channel list as **UI placeholders in v1** — no backend wired up yet
  (SIPSorcery integration is future work, §10), but the layout should already accommodate them.

### 9.3 Theming
- **Dark theme only for v1**, but colors must be defined so they're trivially swappable — use
  Avalonia's dynamic resource/theme-dictionary system rather than hardcoded colors anywhere in
  markup, specifically so user-created custom themes are a realistic post-v1 feature rather than a
  rewrite.
- Currently `App.axaml` sets `RequestedThemeVariant="Dark"` on the `Application` and loads a bare
  `<FluentTheme />`. There is **no theme dictionary or color resource file yet** — the first feature
  that needs a real color must introduce one and pull from it via `DynamicResource`, rather than
  putting a literal color into markup and deferring the extraction.

### 9.4 Messages
- **Markdown rendering is in scope for v1** (bold/italic/code blocks at minimum).

### 9.5 Local Client State
Two separate local stores, split by sensitivity:
- **Identities** (keypairs) → OS-native secure storage, per §4.2.
- **Everything else** (known servers, connection history, friends list, DM history, cached session
  tokens) → **SQLite** via `Microsoft.Data.Sqlite`. Chosen for its tooling and inspectability; the
  native dependency it brings is not a new cost, since NSec already introduced one.
- Known-servers list should support **export/import as a plain file** — user-initiated, no service
  involved, same philosophy as key file transfer.

### 9.6 Distribution & Auto-Update

Client ships as a **downloaded installer per platform**, distributed via **GitHub Releases** —
not a store/marketplace. Packaging and updates are handled by **Velopack**, which produces:
- **Windows**: `Setup.exe` (Velopack's default), with `.msi` also available via its WiX 5
  integration if Group Policy / `Program Files` deployment matters later.
- **macOS**: `.pkg` installer — note this is **not** a `.dmg`; Velopack's native output is `.pkg`
  because it needs installer-level hooks for self-updating, which a plain `.dmg` doesn't support.
  Functionally the same "double-click to install" experience, just a different container format
  than originally assumed.
- **Linux**: self-updating `.AppImage` — no traditional installer package on Linux by design (this
  matches standard AppImage distribution practice, not a Velopack limitation).

**Auto-update**: built in via Velopack's `UpdateManager`, checking a GitHub Releases source for new
versions, with delta-package updates (only changed bytes are downloaded, not the full app each
time). Client checks on launch at minimum; consider a periodic background check too.

**Code signing**: not strictly required by Velopack, but strongly recommended before any public
release — unsigned installers trigger Windows SmartScreen and macOS Gatekeeper warnings that will
read as "this app is sketchy" to new users, which matters a lot for an early-stage self-hosted
project trying to build trust. Budget for a code-signing certificate (Windows) and Apple
Developer ID (macOS) before a public v1 launch, even if internal/beta builds skip it.

## 10. Voice (Future — not v1)

- Library: SIPSorcery (WebRTC/RTP/ICE/DTLS-SRTP in C#). Note it doesn't include cross-platform
  mic/speaker capture out of the box — pair with an SDL2-based audio package or similar for
  Linux/Mac/Windows parity.
- Treat as a **separate subsystem** from chat/SignalR from the start (mirrors how Discord itself
  splits voice infra from its main API) — even if it initially runs in the same process, this
  keeps the door open for self-hosters to run voice on a separate box later.

## 11. V1 Scope vs. Backlog

**In scope for v1:**
- Ed25519 + X25519 identity, generate + import flows, OS-native secure storage
- Challenge-response auth, JWT session tokens
- Dynamic roles/permissions (Owner/Admin/Member defaults)
- Room chat (plaintext-to-server), Markdown rendering, file/image attachments
- Postgres persistence for room messages
- 1:1 E2E encrypted DMs (static ECDH, no forward secrecy), client-side history
- Offline DM mailbox (fan-out queue, per §7.4)
- Push presence (SignalR groups)
- Friends (add-by-pubkey, no approval, block instead)
- Client-side blocking and the first-contact prompt for unknown senders (§7.5)
- Encrypted key backup/export
- Local vault lock with crypto-shred wipe
- Docker deployment + first-run setup wizard → Owner assignment
- Discord-esque Avalonia client, dark theme (theme-ready), voice UI placeholder only
- Client packaged as per-OS installers (Velopack) with built-in auto-update, via GitHub Releases

**Explicitly deferred / backlog:**
- SSH certificate support (not just raw keys)
- Key rotation/expiration policies
- Forward secrecy / Double Ratchet for DMs
- Multi-device "approve from existing device" linking flow (current model: reuse the same key file)
- Voice backend (SIPSorcery wiring)
- Group E2E encryption (MLS) — out of scope entirely per current design; only 1:1 DMs are encrypted
- Server-side block-list enforcement for the offline mailbox queue
- Single-use invite links for password-gated and allowlist servers (§5.2.2)
- Light theme / full custom theming UI
- Mobile clients (Avalonia supports the targets; no current plan to build them)

## 12. Open Decisions (flagged, not blocking)

- Exact audit log scope (which actions get logged) — §5.5.4 records what is logged today and what
  the floor still owes. The remaining question is whether anything beyond moderation and
  administration belongs in it.
- Whether room messages ever get an edit/delete history model, or hard-delete only.
- Session token lifetime, and whether a refresh token exists at all or re-running
  challenge-response *is* the refresh path (#13). Re-running it is cheap — one round trip, no
  refresh-token storage, no second credential to leak — and is the likely answer, but it should be
  written down as a decision rather than arrived at by default.
- Whether a client pins a server's id on first connect (§4.7.2). Without pinning, first contact is
  trust-on-first-use; with it, a changed id is a visible warning rather than a silent one. Belongs
  with the client login work (#14).

Settled, and no longer open: test framework (**xUnit**), solution format (**`.slnx`**), target
framework (**`net10.0`**), client MVVM library (**CommunityToolkit.Mvvm**), server ORM
(**EF Core**, §5.4), client local storage (**SQLite**, §9.5), whether permission keys are data
or code (**code**, §5.3.1), and what a login signature is scoped to (**the server's UUID, not its
hostname**, §4.7.2).
