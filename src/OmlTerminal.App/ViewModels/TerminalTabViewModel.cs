using OmlTerminal.Core.Models;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.App.ViewModels;

public sealed class TerminalTabViewModel(SessionProfile profile, TerminalSession session)
{
    private int _connectStarted;

    public SessionProfile Profile { get; } = profile;
    public TerminalSession Session { get; } = session;

    /// <summary>Non-null while this tab is logging its output to a transcript file.</summary>
    public SessionLogger? Logger { get; set; }

    public string ProtocolText => Profile.Protocol switch
    {
        ProtocolKind.Ssh when Profile.Engine == TransportEngine.Plink => "SSH (PuTTY)",
        ProtocolKind.Ssh when Profile.Engine == TransportEngine.OpenSsh => Profile.X11Forwarding ? "SSH (OpenSSH · X11)" : "SSH (OpenSSH)",
        ProtocolKind.Ssh => "SSH",
        ProtocolKind.Local => string.IsNullOrWhiteSpace(Profile.LocalShellPath) ? "SHELL" : $"SHELL · {Path.GetFileNameWithoutExtension(Profile.LocalShellPath)}",
        var p => p.ToString(),
    };

    public string HostText => Profile.Protocol switch
    {
        ProtocolKind.Serial => $"{Profile.SerialPortName} @ {Profile.BaudRate} baud",
        ProtocolKind.Local => $"{Profile.LocalShellPath} {Profile.LocalShellArgs}".Trim() is { Length: > 0 } cmd ? cmd : "local shell",
        _ when string.IsNullOrEmpty(Profile.Username) => $"{Profile.Host}:{Profile.Port}",
        _ => $"{Profile.Username}@{Profile.Host}:{Profile.Port}",
    };

    private string ConnectingText => Profile.Protocol switch
    {
        ProtocolKind.Serial => $"Opening {Profile.SerialPortName} at {Profile.BaudRate} baud...",
        ProtocolKind.Local => $"Starting {ProtocolText.ToLowerInvariant()}...",
        _ => $"Connecting to {Profile.Host}:{Profile.Port} ({ProtocolText})...",
    };

    /// <summary>Connects once, using the size the control measured. Failures are shown in the terminal itself.</summary>
    public async Task ConnectAsync(int cols, int rows)
    {
        if (Interlocked.Exchange(ref _connectStarted, 1) != 0) return;
        Session.Engine.WriteLocal($"\x1b[36m{ConnectingText}\x1b[0m\r\n");
        StartLoginAutomation();
        try
        {
            await Session.ConnectAsync(cols, rows);
        }
        catch (Exception ex)
        {
            Session.Engine.WriteLocal($"\x1b[31mConnection failed: {ex.Message}\x1b[0m\r\n");
            return;
        }
        if (!string.IsNullOrWhiteSpace(Profile.StartupCommands)) _ = RunStartupCommandsAsync();
    }

    /// <summary>Types the profile's login script once the far end has had a moment to print its banner and prompt.</summary>
    private LoginAutomator? _login;

    /// <summary>Answers Telnet/serial "Username:"/"Password:" prompts and runs "enable" with the saved (or password-
    /// manager) credentials. Subscribed before connecting, so the very first prompt is caught.</summary>
    private void StartLoginAutomation()
    {
        bool inBand = Profile.Protocol is ProtocolKind.Telnet or ProtocolKind.Serial;
        if (!inBand && Profile.Protocol != ProtocolKind.Ssh) return;
        var automator = new LoginAutomator(Profile.Username, Profile.Password, Profile.EnablePassword, answerLogin: inBand);
        if (automator.IsDone) return;
        _login = automator;
        void OnOutput(byte[] data)
        {
            var text = System.Text.Encoding.UTF8.GetString(AnsiStripper.Strip(data));
            if (automator.OnOutput(text) is { } reply) Session.Transport.Write(System.Text.Encoding.UTF8.GetBytes(reply));
            if (automator.IsDone) Session.OutputReceived -= OnOutput;
        }
        Session.OutputReceived += OnOutput;
    }

    private async Task RunStartupCommandsAsync()
    {
        await Task.Delay(1500);
        // Don't type the login script into a "Username:" or enable prompt.
        for (int i = 0; i < 40 && _login is { IsDone: false }; i++) await Task.Delay(250);
        foreach (var line in Core.TextLines.Split(Profile.StartupCommands))
        {
            if (!Session.IsConnected) return;
            if (line.Trim().Length == 0) continue;
            Session.Send(System.Text.Encoding.UTF8.GetBytes(line.TrimEnd() + "\r"));
            await Task.Delay(350);
        }
    }

    public void Close()
    {
        Logger?.Dispose();
        Session.Dispose();
    }
}
