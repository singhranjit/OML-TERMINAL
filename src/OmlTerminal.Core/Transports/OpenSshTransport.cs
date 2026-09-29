using System.Text;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Transports;

/// <summary>
/// SSH through Windows' built-in OpenSSH client (ssh.exe) running in a ConPTY. It's the engine for X11 forwarding
/// (SSH.NET has no X11 channel support), and it picks up ~/.ssh/config, ssh-agent and whatever algorithms the local
/// OpenSSH build supports. Host-key questions are left to the user in the terminal, exactly as in a real console;
/// the saved password / key passphrase is typed in automatically the first time ssh.exe prompts for it.
/// </summary>
public sealed class OpenSshTransport : ITerminalTransport
{
    private readonly LocalTransport _inner;
    private readonly string _password;
    private readonly string _passphrase;
    private readonly StringBuilder _tail = new();
    private bool _passwordSent, _passphraseSent;
    private bool _promptPhaseOver;
    private int _scanned;

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public OpenSshTransport(SessionProfile p, string? display = null)
    {
        var exe = FindSsh() ?? throw new FileNotFoundException(
            "ssh.exe not found. Enable Windows' OpenSSH Client (Settings → Apps → Optional features) or pick another engine.");
        _password = p.Password;
        _passphrase = p.PrivateKeyPassphrase;
        var env = display is null ? null : new Dictionary<string, string> { ["DISPLAY"] = display };
        _inner = new LocalTransport(exe, null, BuildArguments(p), env);
        _inner.DataReceived += OnData;
        _inner.Closed += e => Closed?.Invoke(e);
    }

    public static string? FindSsh()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidates = new[]
        {
            Path.Combine(sys, @"OpenSSH\ssh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OpenSSH\ssh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\usr\bin\ssh.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string BuildArguments(SessionProfile p)
    {
        var args = new List<string> { "-o", "ServerAliveInterval=30" };
        if (p.X11Forwarding) args.Add("-Y");
        if (p.Port is > 0 and not 22) { args.Add("-p"); args.Add(p.Port.ToString()); }
        if (!string.IsNullOrWhiteSpace(p.Username)) { args.Add("-l"); args.Add(p.Username); }
        if (p.AuthMethod == SshAuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(p.PrivateKeyPath))
        {
            args.Add("-i"); args.Add(p.PrivateKeyPath);
        }
        args.Add(p.Host);
        return string.Join(' ', args.Select(Quote));
    }

    private static string Quote(string a) => a.Length == 0 || a.Any(c => c is ' ' or '\t' or '"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a;

    private void OnData(byte[] data)
    {
        DataReceived?.Invoke(data);
        if (_promptPhaseOver) return;

        _tail.Append(Encoding.UTF8.GetString(data));
        if (_tail.Length > 512) _tail.Remove(0, _tail.Length - 512);
        var text = _tail.ToString().TrimEnd();

        if (!_passphraseSent && _passphrase.Length > 0 && text.EndsWith(':') && text.Contains("passphrase for key", StringComparison.OrdinalIgnoreCase))
        {
            _passphraseSent = true;
            Answer(_passphrase);
        }
        else if (!_passwordSent && _password.Length > 0 && text.EndsWith("password:", StringComparison.OrdinalIgnoreCase))
        {
            _passwordSent = true;
            Answer(_password);
        }
        // Once login is past, stop scanning: a later "password:" is the remote user's business (sudo, enable...).
        _scanned += data.Length;
        if (_passwordSent || _scanned > 32 * 1024) _promptPhaseOver = true;
    }

    private void Answer(string secret)
    {
        _tail.Clear();
        _inner.Write(Encoding.UTF8.GetBytes(secret + "\r"));
    }

    public Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default) => _inner.ConnectAsync(cols, rows, cancellationToken);
    public void Write(ReadOnlySpan<byte> data) => _inner.Write(data);
    public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
    public void Close() => _inner.Close();
    public void Dispose() => _inner.Dispose();
}
