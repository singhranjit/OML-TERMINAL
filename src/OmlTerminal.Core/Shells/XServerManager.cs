using System.Diagnostics;
using System.Net.Sockets;

namespace OmlTerminal.Core.Shells;

public sealed record XServerInfo(string Name, string Path, string Arguments);

/// <summary>
/// Finds and starts a local X server so SSH sessions using X11 forwarding can show remote GUI apps (Wireshark, xterm,
/// virt-manager...), MobaXterm-style. OML Terminal doesn't ship its own X server; it drives whichever of VcXsrv,
/// Xming or Cygwin/X is installed. A server started outside the app (X410, WSLg-forwarded...) is detected by its
/// listening TCP port and used as-is.
/// </summary>
public sealed class XServerManager
{
    private Process? _process;

    public int Display { get; set; }

    /// <summary>Overrides detection when set (Settings → X server path).</summary>
    public string? ConfiguredPath { get; set; }

    public string DisplayVariable => $"127.0.0.1:{Display}.0";
    public int TcpPort => 6000 + Display;

    public event Action? StateChanged;

    public static IReadOnlyList<XServerInfo> DetectInstalled()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new List<XServerInfo>
        {
            new("VcXsrv", Path.Combine(pf, @"VcXsrv\vcxsrv.exe"), ":{0} -multiwindow -clipboard -wgl -ac"),
            new("VcXsrv", Path.Combine(pf86, @"VcXsrv\vcxsrv.exe"), ":{0} -multiwindow -clipboard -wgl -ac"),
            new("Xming", Path.Combine(pf86, @"Xming\Xming.exe"), ":{0} -multiwindow -clipboard -ac"),
            new("Xming", Path.Combine(pf, @"Xming\Xming.exe"), ":{0} -multiwindow -clipboard -ac"),
            // Cygwin/X only listens on a unix socket unless told otherwise; ssh.exe on Windows needs TCP.
            new("Cygwin/X", @"C:\cygwin64\bin\XWin.exe", ":{0} -multiwindow -clipboard -listen tcp -ac"),
            new("Cygwin/X", @"C:\cygwin\bin\XWin.exe", ":{0} -multiwindow -clipboard -listen tcp -ac"),
        };
        return candidates.Where(c => File.Exists(c.Path)).DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public XServerInfo? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(ConfiguredPath) && File.Exists(ConfiguredPath))
        {
            var name = Path.GetFileNameWithoutExtension(ConfiguredPath);
            var args = name.Equals("XWin", StringComparison.OrdinalIgnoreCase)
                ? ":{0} -multiwindow -clipboard -listen tcp -ac"
                : ":{0} -multiwindow -clipboard -ac";
            return new XServerInfo(name, ConfiguredPath, args);
        }
        return DetectInstalled().FirstOrDefault();
    }

    public bool IsListening()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", TcpPort).Wait(300) && client.Connected;
        }
        catch { return false; }
    }

    public bool OwnsProcess => _process is { HasExited: false };

    /// <summary>Starts the X server if nothing is listening yet. Returns a human-readable status; throws with
    /// install guidance if no X server exists on this machine.</summary>
    public async Task<string> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (IsListening()) return $"X server already running on {DisplayVariable}";
        var server = Resolve() ?? throw new FileNotFoundException(
            "No X server found. Install VcXsrv (https://sourceforge.net/projects/vcxsrv/), Xming, or Cygwin/X " +
            "(xorg-server package), or set the X server path in Settings.");

        var psi = new ProcessStartInfo(server.Path, string.Format(server.Arguments, Display))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(server.Path)!,
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {server.Name}.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => StateChanged?.Invoke();

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (IsListening()) { StateChanged?.Invoke(); return $"{server.Name} started on {DisplayVariable}"; }
            if (_process.HasExited) throw new InvalidOperationException($"{server.Name} exited immediately (code {_process.ExitCode}). Is display :{Display} already taken?");
        }
        StateChanged?.Invoke();
        return $"{server.Name} launched; waiting for it to accept connections on {DisplayVariable}";
    }

    public static bool CanInstall => FindWinget() is not null;

    private static string? FindWinget()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Installs VcXsrv (GPL, github.com/marchaesen/vcxsrv) with winget. Windows shows its own UAC prompt,
    /// so nothing is installed without the user's consent. Returns winget's exit code and output.</summary>
    public static async Task<(bool Ok, string Output)> InstallVcXsrvAsync(CancellationToken ct = default)
    {
        var winget = FindWinget() ?? throw new FileNotFoundException("winget isn't available on this PC - install VcXsrv from https://github.com/marchaesen/vcxsrv/releases.");
        var psi = new ProcessStartInfo(winget)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "install", "--id", "marha.VcXsrv", "-e", "--silent", "--accept-source-agreements", "--accept-package-agreements" })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start winget.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        return (proc.ExitCode == 0 || DetectInstalled().Count > 0, output);
    }

    /// <summary>Stops only an X server this app started - never one the user launched themselves.</summary>
    public void Stop()
    {
        if (_process is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        _process = null;
        StateChanged?.Invoke();
    }
}
