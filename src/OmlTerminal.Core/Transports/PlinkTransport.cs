using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Transports;

/// <summary>
/// Runs PuTTY plink.exe as a child process and treats its stdio as the terminal byte stream.
/// Useful for legacy gear whose SSH algorithms SSH.NET cannot negotiate.
///
/// plink cannot show interactive prompts over a pipe, so it always runs with -batch. Unknown host keys are handled
/// trust-on-first-use inside the terminal: the fingerprint is shown, the user answers y/n, and an accepted fingerprint
/// is remembered (PlinkHostKeyStore) and passed back as -hostkey on later connects. A changed key is never offered for trust.
///
/// Limitations: plink cannot resize its remote PTY over a pipe, and the password is passed on its command line
/// (visible to other processes of the same user while connected).
/// </summary>
public sealed partial class PlinkTransport(
    string host, int port, string username, string password,
    string? plinkPath = null, PlinkHostKeyStore? hostKeys = null) : ITerminalTransport
{
    private readonly PlinkHostKeyStore _hostKeys = hostKeys ?? new PlinkHostKeyStore();
    private readonly object _sync = new();
    private Process? _proc;
    private string? _pendingFingerprint;
    private bool _pendingLegacy;
    private readonly StringBuilder _stderrAcc = new();
    private bool _legacyAllowed;
    private enum Awaiting { None, HostKey, Legacy }
    private Awaiting _awaiting;
    private int _closing;
    private int _raised;

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public static string? FindPlink(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "plink.exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "plink.exe")));
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            candidates.Add(Path.Combine(Environment.GetFolderPath(root), "PuTTY", "plink.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        var exe = FindPlink(plinkPath)
            ?? throw new FileNotFoundException("plink.exe not found. Install PuTTY or set the plink path in Settings.");
        _legacyAllowed = _hostKeys.IsLegacyAllowed(host, port);
        Launch(exe, _hostKeys.Get(host, port));
        return Task.CompletedTask;
    }

    private void Launch(string exe, string? trustedFingerprint)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-ssh");
        psi.ArgumentList.Add("-batch");
        psi.ArgumentList.Add("-t");
        if (_legacyAllowed) { psi.ArgumentList.Add("-load"); psi.ArgumentList.Add(PuttyLegacyProfile.Ensure()); }
        if (trustedFingerprint is not null) { psi.ArgumentList.Add("-hostkey"); psi.ArgumentList.Add(trustedFingerprint); }
        psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(port.ToString());
        if (!string.IsNullOrEmpty(username)) { psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(username); }
        if (!string.IsNullOrEmpty(password)) { psi.ArgumentList.Add("-pw"); psi.ArgumentList.Add(password); }
        psi.ArgumentList.Add(host);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start plink.exe");
        lock (_sync) { _proc = proc; _pendingFingerprint = null; _pendingLegacy = false; _stderrAcc.Clear(); }

        var outPump = Task.Run(() => Pump(proc.StandardOutput.BaseStream, isStderr: false));
        var errPump = Task.Run(() => Pump(proc.StandardError.BaseStream, isStderr: true));
        _ = Task.Run(async () =>
        {
            await proc.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(outPump, errPump).ConfigureAwait(false);
            OnProcessExited(proc);
        });
    }

    [GeneratedRegex(@"SHA256:[A-Za-z0-9+/]+")]
    private static partial Regex FingerprintRegex();

    private void Pump(Stream s, bool isStderr)
    {
        var buf = new byte[8192];
        try
        {
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                var chunk = buf.AsSpan(0, n).ToArray();
                if (isStderr)
                {
                    var text = Encoding.UTF8.GetString(chunk).Replace("\r\n", "\n").Replace("\n", "\r\n");
                    // plink writes these messages in several small pieces, so detect on the accumulated text.
                    string all;
                    lock (_sync) { _stderrAcc.Append(text); all = _stderrAcc.ToString(); }
                    if (all.Contains("is not cached", StringComparison.OrdinalIgnoreCase)
                        && FindFingerprint(all) is { } fp)
                    {
                        lock (_sync) _pendingFingerprint = fp;
                    }
                    if (all.Contains("weak crypto primitive", StringComparison.OrdinalIgnoreCase)
                        || all.Contains("below the configured warning threshold", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_sync) _pendingLegacy = true;
                    }
                    chunk = Encoding.UTF8.GetBytes(text);
                    if (_pendingFingerprint is not null || _pendingLegacy)
                    {
                        // The trailing "FATAL ERROR ... batch mode" is just noise once we are about to ask the user.
                        text = text.Replace("Connection abandoned.", "")
                                   .Replace("FATAL ERROR: Cannot confirm a host key in batch mode", "")
                                   .Replace("FATAL ERROR: Cannot confirm a weak crypto primitive in batch mode", "");
                        chunk = Encoding.UTF8.GetBytes(text);
                        if (text.Trim().Length == 0) continue;
                    }
                }
                DataReceived?.Invoke(chunk);
            }
        }
        catch { }
    }

    private static string? FindFingerprint(string text) => FingerprintRegex().Match(text) is { Success: true } m ? m.Value : null;

    private void OnProcessExited(Process proc)
    {
        if (Volatile.Read(ref _closing) != 0) { RaiseClosed(null); return; }
        string? fp;
        bool legacy;
        lock (_sync)
        {
            if (!ReferenceEquals(proc, _proc)) return;
            fp = _pendingFingerprint;
            legacy = _pendingLegacy && !_legacyAllowed;
            _awaiting = legacy ? Awaiting.Legacy : fp is not null ? Awaiting.HostKey : Awaiting.None;
        }
        if (legacy) Emit("\r\n\x1b[33mThis device only offers weak/legacy SSH crypto. Allow it for this host and connect? (y/N): \x1b[0m");
        else if (fp is not null) Emit("\r\n\x1b[33mTrust this host key and connect? (y/N): \x1b[0m");
        else RaiseClosed(null);
    }

    private void Emit(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        Awaiting awaiting;
        lock (_sync) awaiting = _awaiting;
        if (awaiting != Awaiting.None) { HandleAnswer(data, awaiting); return; }

        if (_proc is not { HasExited: false } p) return;
        var copy = data.ToArray();
        try { p.StandardInput.BaseStream.Write(copy, 0, copy.Length); p.StandardInput.BaseStream.Flush(); } catch { }
    }

    private void HandleAnswer(ReadOnlySpan<byte> data, Awaiting kind)
    {
        foreach (var b in data)
        {
            if (b is (byte)'y' or (byte)'Y')
            {
                string? fp;
                lock (_sync) { fp = _pendingFingerprint; _awaiting = Awaiting.None; }
                Emit("y\r\n");
                try
                {
                    if (kind == Awaiting.Legacy) { _hostKeys.AllowLegacy(host, port); _legacyAllowed = true; fp = _hostKeys.Get(host, port); }
                    else if (fp is not null) _hostKeys.Set(host, port, fp);
                    Launch(FindPlink(plinkPath)!, fp);
                }
                catch (Exception ex)
                {
                    Emit($"\r\n\x1b[31m{ex.Message}\x1b[0m\r\n");
                    RaiseClosed(ex.Message);
                }
                return;
            }
            if (b is (byte)'n' or (byte)'N' or 13 or 10)
            {
                Emit("n\r\n");
                lock (_sync) _awaiting = Awaiting.None;
                RaiseClosed(null);
                return;
            }
        }
    }

    public void Resize(int cols, int rows) { }

    public void Close()
    {
        Interlocked.Exchange(ref _closing, 1);
        try { if (_proc is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch { }
        RaiseClosed(null);
    }

    private void RaiseClosed(string? error)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 0) Closed?.Invoke(error);
    }

    public void Dispose()
    {
        Close();
        _proc?.Dispose();
    }
}
