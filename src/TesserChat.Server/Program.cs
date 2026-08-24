using TesserChat.Server.Accounts;
using TesserChat.Server.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddPersistence();
builder.AddAccounts();

var app = builder.Build();

// Before serving anything, so an upgraded deployment never answers requests against last
// version's schema. Controlled by Database:MigrateOnStartup (§5.4).
await app.ApplyMigrationsAsync();

// Liveness probe for the Docker deployment path. Deliberately unauthenticated — it reports
// nothing about the server's members, config, or identity.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

// Exposed so TesserChat.Server.Tests can boot the real host via WebApplicationFactory<Program>.
public partial class Program;
