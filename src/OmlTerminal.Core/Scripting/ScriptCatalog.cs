using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Scripting;

/// <summary>One script found in the scripts folder. Running it (<see cref="ScriptRunner"/>) launches Interpreter as
/// a normal external process - the same trust level as the user running it themselves from a shell, not an
/// in-process plugin with access to the app's own memory (credential vault, live sessions, etc).</summary>
public sealed record ScriptDefinition(string Path, string Name, string Description, string Interpreter, IReadOnlyList<string> InterpreterArgs);

/// <summary>Scans a folder for scripts by file extension - no manifest file required. A leading
/// "# Description: ..." (or "// Description: ...") comment line, if present, becomes the subtitle shown in the UI.</summary>
public static class ScriptCatalog
{
    /// <summary>Interpreter per extension for this OS. On Linux/macOS: PowerShell scripts need PowerShell 7 (pwsh)
    /// installed, Python is python3, and Windows batch files aren't offered at all.</summary>
    private static readonly Dictionary<string, (string Interpreter, string[] Args)> Interpreters = OperatingSystem.IsWindows()
        ? new(StringComparer.OrdinalIgnoreCase)
        {
            [".ps1"] = ("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]),
            [".py"] = ("python", []),
            [".cmd"] = ("cmd.exe", ["/c"]),
            [".bat"] = ("cmd.exe", ["/c"]),
            [".sh"] = ("bash", []),
        }
        : new(StringComparer.OrdinalIgnoreCase)
        {
            [".ps1"] = ("pwsh", ["-NoProfile", "-File"]),
            [".py"] = ("python3", []),
            [".sh"] = ("/bin/bash", []),
        };

    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "scripts");

    public static IReadOnlyList<ScriptDefinition> Scan(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var results = new List<ScriptDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!Interpreters.TryGetValue(System.IO.Path.GetExtension(file), out var runner)) continue;
            results.Add(new ScriptDefinition(file, System.IO.Path.GetFileNameWithoutExtension(file), ReadDescription(file), runner.Interpreter, runner.Args));
        }
        return results.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReadDescription(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file).Take(5))
            {
                var t = line.TrimStart('#', '/', ' ');
                if (t.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                    return t["Description:".Length..].Trim();
            }
        }
        catch { /* unreadable file - just show it with no description */ }
        return "";
    }
}
