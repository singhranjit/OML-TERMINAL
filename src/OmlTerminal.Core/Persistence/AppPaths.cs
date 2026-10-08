namespace OmlTerminal.Core.Persistence;

public static class AppPaths
{
    /// <summary>~/.oml-terminal, or the folder in OML_TERMINAL_HOME (portable installs, demos, a second profile).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("OML_TERMINAL_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oml-terminal");
}
