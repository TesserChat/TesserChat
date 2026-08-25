using System.Security.Claims;
using TesserChat.Server;
using TesserChat.Server.Accounts;
using TesserChat.Server.Auditing;
using TesserChat.Server.Auth;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;
using TesserChat.Server.Realtime;
using TesserChat.Server.Rooms;
using TesserChat.Server.Setup;

// `hash-join-secret <password>` prints the value for Connection:JoinSecretHash and exits, so an
// operator setting up a password-gated server (§5.2) has a way to produce one without a
// side-application. Handled before the host is built: it needs no database and no configuration.
if (JoinSecretCommand.TryHandle(args, Console.Out, Console.Error, out var exitCode))
{
    return exitCode;
}

// `healthcheck` probes /health and exits — the container's HEALTHCHECK (§5.6). The runtime image
// has no curl, so the probe is the app's own binary.
if (HealthCheckCommand.TryHandle(args, Console.Error, out exitCode))
{
    return exitCode;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddPersistence();
builder.AddAccounts();
builder.AddRolesAndPermissions();
builder.AddAuditing();
builder.AddChallengeAuth();
builder.AddRealtime();
builder.AddRooms();
builder.AddSetup();

var app = builder.Build();

// Before serving anything, so an upgraded deployment never answers requests against last
// version's schema. Controlled by Database:MigrateOnStartup (§5.4).
await app.ApplyMigrationsAsync();

// After migrations, since it reads a table they create. Says in the log whether the server still
// needs setting up (§5.6) — a self-hoster's first contact with a fresh container is `docker logs`.
await app.ReportSetupStateAsync();

// Order matters and is not the order they are declared in: authentication runs first so that
// authorization has a principal to decide about.
app.UseAuthentication();
app.UseAuthorization();

// Liveness probe for the Docker deployment path. Deliberately unauthenticated — it reports
// nothing about the server's members, config, or identity.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Challenge-response login (§4.7). Both endpoints are necessarily anonymous — they are how a
// member authenticates in the first place.
app.MapLogin();

// The authenticated caller's own account id. Small on purpose: it is what a client calls to
// confirm a token works, and the first endpoint proving the bearer scheme is wired up. It tells a
// caller only what they already proved by holding the token.
app.MapGet("/auth/session", (ClaimsPrincipal principal) =>
{
    var accountId = principal.GetAccountId();

    return accountId is null
        ? Results.Unauthorized()
        : Results.Ok(new { accountId = accountId.Value.ToString("D") });
}).RequireAuthorization();

// The real-time transport room chat and presence both ride on (§6). Mapped after authentication
// is in the pipeline, since the hub authorises its own handshake.
app.MapRealtime();

await app.RunAsync();

return 0;

// Exposed so TesserChat.Server.Tests can boot the real host via WebApplicationFactory<Program>.
public partial class Program;
