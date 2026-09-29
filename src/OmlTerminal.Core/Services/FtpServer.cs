using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OmlTerminal.Core.Services;

/// <summary>
/// A small FTP server rooted in one folder - the tftpd64 FTP use case: let a device or PC upload/download files over
/// FTP. Passive mode (PASV) only, which works through NAT and firewalls; anonymous or a single username/password.
/// Path traversal outside the root is refused. Not TLS - use on a trusted LAN.
/// </summary>
public sealed class FtpServer : NetworkServiceBase
{
    private readonly int _port;
    private TcpListener? _listener;

    public string RootDirectory { get; }
    public string BindAddress { get; }
    public string? Username { get; }
    public string? Password { get; }
    public bool AllowAnonymous => Username is null;
    public bool ReadOnly { get; }
    /// <summary>Passive data ports handed out to clients. Keep a small range you can open on a firewall.</summary>
    public (int From, int To) PassivePorts { get; init; } = (50000, 50100);

    public override string Name => "FTP server";

    public FtpServer(string rootDirectory, string bindAddress = "0.0.0.0", int port = 21, string? username = null, string? password = null, bool readOnly = false)
    {
        RootDirectory = rootDirectory;
        BindAddress = bindAddress;
        _port = port;
        Username = username;
        Password = password;
        ReadOnly = readOnly;
    }

    protected override void OnStart()
    {
        Directory.CreateDirectory(RootDirectory);
        _listener = new TcpListener(IPAddress.Parse(BindAddress), _port);
        _listener.Start();
        Log(LogLevel.Info, "", $"Listening on {BindAddress}:{_port}, root {RootDirectory}{(AllowAnonymous ? " (anonymous)" : $" (user {Username})")}{(ReadOnly ? " [read-only]" : "")}");
        _ = Task.Run(AcceptLoopAsync);
    }

    protected override void OnStop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        while (KeepRunning && listener is not null)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => new FtpSession(this, client).RunAsync());
        }
    }

    internal void Report(LogLevel level, IPEndPoint? peer, string message) => Log(level, peer, message);

    /// <summary>Resolves a client path (absolute from the FTP root, or relative to the session dir) to a real path
    /// inside the root, or null if it escapes the root.</summary>
    internal string? Resolve(string sessionDir, string requested)
    {
        var combined = requested.StartsWith('/') ? requested : sessionDir.TrimEnd('/') + "/" + requested;
        var full = Path.GetFullPath(Path.Combine(RootDirectory, combined.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(RootDirectory);
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    internal IPAddress LocalAddressFor(IPAddress client)
    {
        if (BindAddress != "0.0.0.0") return IPAddress.Parse(BindAddress);
        // Reply with an address the client can reach - the primary LAN IP.
        return IPAddress.TryParse(LocalAddresses.PrimaryIpv4(), out var ip) ? ip : IPAddress.Loopback;
    }
}

/// <summary>One control connection. Speaks just enough FTP for real clients (Windows, WinSCP, FileZilla, IOS 'copy ftp').</summary>
internal sealed class FtpSession(FtpServer server, TcpClient control)
{
    private readonly IPEndPoint _peer = (IPEndPoint)control.Client.RemoteEndPoint!;
    private StreamWriter _out = null!;
    private string _dir = "/";
    private bool _authenticated;
    private string? _pendingUser;
    private TcpListener? _pasv;
    private string _renameFrom = "";

    public async Task RunAsync()
    {
        try
        {
            using var _ = control;
            var stream = control.GetStream();
            _out = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);
            server.Report(LogLevel.Info, _peer, "Connected");
            await Send(220, "OML Terminal FTP ready");

            string? line;
            while (server.IsRunning && (line = await reader.ReadLineAsync()) is not null)
            {
                var space = line.IndexOf(' ');
                var cmd = (space < 0 ? line : line[..space]).ToUpperInvariant();
                var arg = space < 0 ? "" : line[(space + 1)..];
                if (cmd is not ("PASS")) server.Report(LogLevel.Receive, _peer, $"{cmd} {(cmd == "PASS" ? "***" : arg)}".Trim());
                if (!await Handle(cmd, arg)) break;
            }
        }
        catch (IOException) { }
        catch (Exception ex) { server.Report(LogLevel.Error, _peer, ex.Message); }
        finally { try { _pasv?.Stop(); } catch { } server.Report(LogLevel.Info, _peer, "Disconnected"); }
    }

    private async Task<bool> Handle(string cmd, string arg)
    {
        if (!_authenticated && cmd is not ("USER" or "PASS" or "QUIT" or "FEAT" or "SYST" or "OPTS" or "AUTH"))
        {
            await Send(530, "Log in first");
            return true;
        }
        switch (cmd)
        {
            case "USER":
                _pendingUser = arg;
                if (server.AllowAnonymous) { _authenticated = true; await Send(230, "Anonymous access granted"); }
                else await Send(331, "Password required");
                return true;
            case "PASS":
                _authenticated = server.AllowAnonymous || (_pendingUser == server.Username && arg == server.Password);
                await Send(_authenticated ? 230 : 530, _authenticated ? "Logged in" : "Login incorrect");
                return true;
            case "SYST": await Send(215, "UNIX Type: L8"); return true;
            case "FEAT": await SendFeat(); return true;
            case "OPTS": await Send(200, "OK"); return true;
            case "TYPE": await Send(200, $"Type set to {arg}"); return true;
            case "PWD": case "XPWD": await Send(257, $"\"{_dir}\" is the current directory"); return true;
            case "CWD": case "XCWD": return await ChangeDir(arg);
            case "CDUP": return await ChangeDir("..");
            case "PASV": await OpenPasv(); return true;
            case "LIST": await Transfer(() => Listing(arg, full: true)); return true;
            case "NLST": await Transfer(() => Listing(arg, full: false)); return true;
            case "RETR": await Retrieve(arg); return true;
            case "STOR": await Store(arg); return true;
            case "SIZE": await SizeCmd(arg); return true;
            case "DELE": await Delete(arg); return true;
            case "MKD": case "XMKD": await MakeDir(arg); return true;
            case "RMD": case "XRMD": await RemoveDir(arg); return true;
            case "RNFR": _renameFrom = arg; await Send(350, "Ready for RNTO"); return true;
            case "RNTO": await Rename(arg); return true;
            case "NOOP": await Send(200, "OK"); return true;
            case "QUIT": await Send(221, "Bye"); return false;
            default: await Send(502, $"{cmd} not implemented"); return true;
        }
    }

    private async Task SendFeat()
    {
        await _out.WriteLineAsync("211-Features:");
        await _out.WriteLineAsync(" PASV");
        await _out.WriteLineAsync(" SIZE");
        await _out.WriteLineAsync(" UTF8");
        await Send(211, "End");
    }

    private async Task<bool> ChangeDir(string arg)
    {
        var target = arg == ".." ? ParentOf(_dir) : (arg.StartsWith('/') ? arg : _dir.TrimEnd('/') + "/" + arg);
        var real = server.Resolve("/", target);
        if (real is null || !Directory.Exists(real)) { await Send(550, "No such directory"); return true; }
        _dir = "/" + Path.GetRelativePath(server.RootDirectory, real).Replace('\\', '/').TrimStart('.').TrimStart('/');
        if (_dir == "/") { } else _dir = "/" + _dir.Trim('/');
        await Send(250, $"Directory changed to {_dir}");
        return true;
    }

    private static string ParentOf(string dir)
    {
        var t = dir.TrimEnd('/');
        int i = t.LastIndexOf('/');
        return i <= 0 ? "/" : t[..i];
    }

    private async Task OpenPasv()
    {
        _pasv?.Stop();
        var (from, to) = server.PassivePorts;
        for (int port = from; port <= to; port++)
        {
            try
            {
                _pasv = new TcpListener(IPAddress.Any, port);
                _pasv.Start();
                var ip = server.LocalAddressFor(_peer.Address).GetAddressBytes();
                await Send(227, $"Entering Passive Mode ({ip[0]},{ip[1]},{ip[2]},{ip[3]},{port / 256},{port % 256})");
                return;
            }
            catch (SocketException) { }
        }
        await Send(425, "No free passive port");
    }

    private async Task Transfer(Func<string> body)
    {
        if (_pasv is null) { await Send(425, "Use PASV first"); return; }
        await Send(150, "Opening data connection");
        try
        {
            using var data = await _pasv.AcceptTcpClientAsync();
            using var writer = new StreamWriter(data.GetStream(), new UTF8Encoding(false)) { NewLine = "\r\n" };
            await writer.WriteAsync(body());
            await writer.FlushAsync();
        }
        finally { _pasv.Stop(); _pasv = null; }
        await Send(226, "Transfer complete");
    }

    private string Listing(string arg, bool full)
    {
        var real = server.Resolve(_dir, string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-') ? "" : arg) ?? server.RootDirectory;
        if (!Directory.Exists(real)) return "";
        var sb = new StringBuilder();
        foreach (var d in Directory.GetDirectories(real))
            sb.Append(full ? $"drwxr-xr-x 1 owner group 0 {Fmt(Directory.GetLastWriteTime(d))} {Path.GetFileName(d)}\r\n" : Path.GetFileName(d) + "\r\n");
        foreach (var f in Directory.GetFiles(real))
        {
            var fi = new FileInfo(f);
            sb.Append(full ? $"-rw-r--r-- 1 owner group {fi.Length} {Fmt(fi.LastWriteTime)} {fi.Name}\r\n" : fi.Name + "\r\n");
        }
        return sb.ToString();
    }

    private static string Fmt(DateTime t) => t.ToString("MMM dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private async Task Retrieve(string arg)
    {
        var real = server.Resolve(_dir, arg);
        if (real is null || !File.Exists(real)) { await Send(550, "File not found"); return; }
        if (_pasv is null) { await Send(425, "Use PASV first"); return; }
        await Send(150, $"Sending {Path.GetFileName(real)}");
        try
        {
            using var data = await _pasv.AcceptTcpClientAsync();
            await using var file = File.OpenRead(real);
            await file.CopyToAsync(data.GetStream());
        }
        finally { _pasv.Stop(); _pasv = null; }
        await Send(226, "Transfer complete");
        server.Report(LogLevel.Send, _peer, $"Sent {arg}");
    }

    private async Task Store(string arg)
    {
        if (server.ReadOnly) { await Send(550, "Server is read-only"); return; }
        var real = server.Resolve(_dir, arg);
        if (real is null) { await Send(553, "Illegal path"); return; }
        if (_pasv is null) { await Send(425, "Use PASV first"); return; }
        await Send(150, $"Receiving {Path.GetFileName(real)}");
        try
        {
            using var data = await _pasv.AcceptTcpClientAsync();
            await using var file = File.Create(real);
            await data.GetStream().CopyToAsync(file);
        }
        finally { _pasv.Stop(); _pasv = null; }
        await Send(226, "Transfer complete");
        server.Report(LogLevel.Success, _peer, $"Received {arg}");
    }

    private async Task SizeCmd(string arg)
    {
        var real = server.Resolve(_dir, arg);
        if (real is null || !File.Exists(real)) { await Send(550, "File not found"); return; }
        await Send(213, new FileInfo(real).Length.ToString());
    }

    private async Task Delete(string arg)
    {
        if (server.ReadOnly) { await Send(550, "Server is read-only"); return; }
        var real = server.Resolve(_dir, arg);
        if (real is null || !File.Exists(real)) { await Send(550, "File not found"); return; }
        File.Delete(real);
        await Send(250, "Deleted");
    }

    private async Task MakeDir(string arg)
    {
        if (server.ReadOnly) { await Send(550, "Server is read-only"); return; }
        var real = server.Resolve(_dir, arg);
        if (real is null) { await Send(550, "Illegal path"); return; }
        Directory.CreateDirectory(real);
        await Send(257, $"\"{arg}\" created");
    }

    private async Task RemoveDir(string arg)
    {
        if (server.ReadOnly) { await Send(550, "Server is read-only"); return; }
        var real = server.Resolve(_dir, arg);
        if (real is null || !Directory.Exists(real)) { await Send(550, "No such directory"); return; }
        Directory.Delete(real, false);
        await Send(250, "Removed");
    }

    private async Task Rename(string arg)
    {
        if (server.ReadOnly) { await Send(550, "Server is read-only"); return; }
        var from = server.Resolve(_dir, _renameFrom);
        var to = server.Resolve(_dir, arg);
        if (from is null || to is null || !File.Exists(from)) { await Send(550, "Rename failed"); return; }
        File.Move(from, to, true);
        await Send(250, "Renamed");
    }

    private Task Send(int code, string message)
    {
        server.Report(code >= 400 ? LogLevel.Warning : LogLevel.Info, _peer, $"{code} {message}");
        return _out.WriteLineAsync($"{code} {message}");
    }
}
