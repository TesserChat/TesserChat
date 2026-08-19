var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness probe for the Docker deployment path. Deliberately unauthenticated — it reports
// nothing about the server's members, config, or identity.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so TesserChat.Server.Tests can boot the real host via WebApplicationFactory<Program>.
public partial class Program;
