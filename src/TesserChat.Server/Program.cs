using TesserChat.Server.Accounts;
using TesserChat.Server.Authorization;
using TesserChat.Server.Persistence;

// `hash-join-secret <password>` prints the value for Connection:JoinSecretHash and exits, so an
// operator setting up a password-gated server (§5.2) has a way to produce one without a
// side-application. Handled before the host is built: it needs no database and no configuration.
if (JoinSecretCommand.TryHandle(args, Console.Out, Console.Error, out var exitCode))
{
    return exitCode;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddPersistence();
builder.AddAccounts();
builder.AddRolesAndPermissions();

var app = builder.Build();

// Before serving anything, so an upgraded deployment never answers requests against last
// version's schema. Controlled by Database:MigrateOnStartup (§5.4).
await app.ApplyMigrationsAsync();

// Liveness probe for the Docker deployment path. Deliberately unauthenticated — it reports
// nothing about the server's members, config, or identity.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

return 0;

// Exposed so TesserChat.Server.Tests can boot the real host via WebApplicationFactory<Program>.
public partial class Program;
