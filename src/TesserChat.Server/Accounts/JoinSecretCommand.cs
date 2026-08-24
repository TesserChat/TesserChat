namespace TesserChat.Server.Accounts;

/// <summary>
/// The <c>hash-join-secret</c> startup command (§5.2).
/// </summary>
/// <remarks>
/// An operator configuring a password-gated server needs the hash of their joining password, and
/// <see cref="ConnectionOptions.JoinSecretHash"/> deliberately never accepts the password itself.
/// Without this they would have to write a program to produce one, so the server produces it:
/// <c>dotnet run -- hash-join-secret "the password"</c>, or the same argument on the container.
/// </remarks>
internal static class JoinSecretCommand
{
    private const string CommandName = "hash-join-secret";

    /// <summary>
    /// Handles the command if <paramref name="args"/> names it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the command was handled and the process should exit with
    /// <paramref name="exitCode"/>; <see langword="false"/> to boot the server normally.
    /// </returns>
    public static bool TryHandle(string[] args, TextWriter output, TextWriter error, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        exitCode = 0;

        if (args.Length == 0 || !string.Equals(args[0], CommandName, StringComparison.Ordinal))
        {
            return false;
        }

        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine($"usage: {CommandName} <joining-password>");
            error.WriteLine();
            error.WriteLine(
                "Prints the value to set as Connection:JoinSecretHash for a password-gated server.");
            exitCode = 1;
            return true;
        }

        // Printed alone on stdout so it can be piped or captured. The password came in on the
        // command line, which shell history records — the note below says so rather than pretending
        // otherwise.
        output.WriteLine(JoinSecretHasher.Hash(args[1]));
        error.WriteLine();
        error.WriteLine("Set this as Connection:JoinSecretHash, and clear the password from your");
        error.WriteLine("shell history — it was passed on the command line.");

        return true;
    }
}
