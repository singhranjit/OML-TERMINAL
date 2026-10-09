using System.Diagnostics;
using System.Text;

namespace OmlTerminal.Core.Shells;

public enum ShellKind { PowerShell, Pwsh, Cmd, Wsl, GitBash, Cygwin, Msys2, Custom, Bash, Zsh, Fish, Sh }

/// <summary>One launchable local shell: the executable, its arguments, and any environment it needs.</summary>
public sealed record ShellInfo(string Name, ShellKind Kind, string Path, string Arguments = "",
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public override string ToString() => Name;
}

/// <summary>Finds the shells installed on this machine - PowerShell, cmd, WSL distros, Git Bash, Cygwin and MSYS2 -
/// so the Shell/WSL session types can offer a pick-list instead of making people hunt for bash.exe.</summary>
public static class ShellCatalog
{
    /// <summary>Keeps Cygwin/MSYS2 login shells in the start directory instead of jumping to $HOME.</summary>
    private static readonly IReadOnlyDictionary<string, string> StayInCwd = new Dictionary<string, string>
    {
        ["CHERE_INVOKING"] = "1",
    };

    public static IReadOnlyList<ShellInfo> Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            string etcShells = "";
            try { if (File.Exists("/etc/shells")) etcShells = File.ReadAllText("/etc/shells"); } catch { }
            return DetectUnix(etcShells, Environment.GetEnvironmentVariable("SHELL"), File.Exists);
        }
        var list = new List<ShellInfo>();
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        void AddIf(string name, ShellKind kind, string path, string args = "", IReadOnlyDictionary<string, string>? env = null)
        {
            if (File.Exists(path)) list.Add(new ShellInfo(name, kind, path, args, env));
        }

        AddIf("Windows PowerShell", ShellKind.PowerShell, Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe"), "-NoLogo");
        AddIf("PowerShell 7", ShellKind.Pwsh, Path.Combine(pf, @"PowerShell\7\pwsh.exe"), "-NoLogo");
        AddIf("Command Prompt", ShellKind.Cmd, Path.Combine(sys, "cmd.exe"));

        foreach (var distro in WslDistros())
            list.Add(new ShellInfo($"WSL: {distro}", ShellKind.Wsl, Path.Combine(sys, "wsl.exe"), $"-d {distro} --cd ~"));

        AddIf("Git Bash", ShellKind.GitBash, Path.Combine(pf, @"Git\bin\bash.exe"), "--login -i");
        foreach (var root in new[] { @"C:\cygwin64", @"C:\cygwin" })
            AddIf($"Cygwin ({Path.GetFileName(root)})", ShellKind.Cygwin, Path.Combine(root, @"bin\bash.exe"), "--login -i", StayInCwd);
        AddIf("MSYS2 (UCRT64)", ShellKind.Msys2, @"C:\msys64\usr\bin\bash.exe", "--login -i",
            new Dictionary<string, string> { ["CHERE_INVOKING"] = "1", ["MSYSTEM"] = "UCRT64" });

        return list;
    }

    /// <summary>Linux/macOS: the login shell first, then each distinct shell listed in /etc/shells (bash, zsh, fish,
    /// pwsh...). /usr/bin/bash and /bin/bash are the same shell on merged-/usr systems, so names are de-duplicated.</summary>
    public static IReadOnlyList<ShellInfo> DetectUnix(string etcShells, string? loginShell, Func<string, bool> exists)
    {
        var list = new List<ShellInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path, bool login)
        {
            var name = System.IO.Path.GetFileName(path);
            if (string.IsNullOrEmpty(name) || !exists(path) || !seen.Add(name)) return;
            // Skip the non-interactive helpers some distros list (git-shell, rbash, nologin).
            if (name is "git-shell" or "rbash" or "nologin" or "false" or "screen" or "tmux") return;
            var (kind, display) = name switch
            {
                "bash" => (ShellKind.Bash, "Bash"),
                "zsh" => (ShellKind.Zsh, "Zsh"),
                "fish" => (ShellKind.Fish, "Fish"),
                "pwsh" => (ShellKind.Pwsh, "PowerShell 7"),
                "sh" or "dash" => (ShellKind.Sh, name == "sh" ? "sh" : "Dash"),
                _ => (ShellKind.Custom, name),
            };
            // Login shells read the user's profile (PATH additions, prompt), as a desktop terminal does.
            var args = kind switch { ShellKind.Bash or ShellKind.Zsh or ShellKind.Fish => "-l", ShellKind.Pwsh => "-NoLogo -Login", _ => "" };
            list.Add(new ShellInfo(login ? $"{display} (login shell)" : display, kind, path, args));
        }

        if (!string.IsNullOrWhiteSpace(loginShell)) Add(loginShell.Trim(), login: true);
        foreach (var raw in etcShells.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            Add(line, login: false);
        }
        if (list.Count == 0 && exists("/bin/sh")) list.Add(new ShellInfo("sh", ShellKind.Sh, "/bin/sh"));
        return list;
    }

    /// <summary>Environment a given shell path needs even when saved as a custom path (e.g. Cygwin's bash).</summary>
    public static IReadOnlyDictionary<string, string>? EnvironmentFor(string shellPath)
    {
        if (string.IsNullOrWhiteSpace(shellPath)) return null;
        var p = shellPath.Replace('/', '\\');
        if (p.Contains(@"\cygwin", StringComparison.OrdinalIgnoreCase)) return StayInCwd;
        if (p.Contains(@"\msys64\", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string> { ["CHERE_INVOKING"] = "1", ["MSYSTEM"] = "UCRT64" };
        return null;
    }

    public static IReadOnlyList<string> WslDistros()
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl)) return [];
        try
        {
            var psi = new ProcessStartInfo(wsl, "--list --quiet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Unicode, // wsl.exe writes UTF-16LE to a pipe
            };
            using var proc = Process.Start(psi);
            if (proc is null) return [];
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { } return []; }
            return ParseWslList(output);
        }
        catch { return []; }
    }

    public static IReadOnlyList<string> ParseWslList(string output) =>
        output.Replace("\0", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.Contains(' ')) // "Windows Subsystem for Linux has no installed distributions." etc.
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
