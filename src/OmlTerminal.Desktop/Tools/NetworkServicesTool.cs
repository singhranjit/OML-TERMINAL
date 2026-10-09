using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Services;

namespace OmlTerminal.Desktop.Tools;

public sealed class NetworkServicesTool : UserControl, IToolView
{
    private const int MaxLogLines = 2000;
    private readonly LinkedList<string> _log = new();

    private TftpServer? _tftp;
    private FtpServer? _ftp;
    private SyslogServer? _syslog;
    private SntpServer? _sntp;
    private DhcpServer? _dhcp;
    private CancellationTokenSource? _clientCts;
    private DispatcherTimer? _leaseTimer;

    private readonly List<string> _addrs = LocalAddresses.Ipv4().ToList();
    private readonly ToggleButton[] _tabs;
    private readonly Control[] _panels;
    private readonly TextBox _logBox = Ui.Output();
    private readonly CheckBox _follow = Ui.Check("Follow", true);
    private readonly TextBlock _running = Ui.Text("No services running", 12, color: Ui.Muted);

    // TFTP server
    private readonly TextBox _tftpRoot = Ui.Input(Path.Combine(AppPaths.DataDirectory, "tftp-root"), mono: true);
    private readonly ComboBox _tftpBind;
    private readonly NumericUpDown _tftpPort = Ui.Number(69, 1, 65535);
    private readonly CheckBox _tftpWrite = Ui.Check("Allow uploads (WRQ)", true), _tftpOverwrite = Ui.Check("Allow overwriting existing files");
    private readonly Button _tftpToggle;
    // TFTP client
    private readonly TextBox _clientHost = Ui.Input("", "10.0.0.1", mono: true), _clientRemote = Ui.Input("", "running-config", mono: true),
        _clientLocal = Ui.Input(Path.Combine(AppPaths.DataDirectory, "downloads", "download.bin"), mono: true);
    private readonly NumericUpDown _clientPort = Ui.Number(69, 1, 65535);
    private readonly Button _get, _put;
    private readonly ProgressBar _clientProgress = new() { IsIndeterminate = true, IsVisible = false };
    private readonly TextBlock _clientStatus = Ui.Text("", 12, color: Ui.Muted);
    // FTP
    private readonly TextBox _ftpRoot = Ui.Input(Path.Combine(AppPaths.DataDirectory, "tftp-root"), mono: true), _ftpUser = Ui.Input(), _ftpPass = new() { PasswordChar = '•' };
    private readonly ComboBox _ftpBind;
    private readonly NumericUpDown _ftpPort = Ui.Number(21, 1, 65535);
    private readonly CheckBox _ftpAnon = Ui.Check("Allow anonymous login", true), _ftpReadOnly = Ui.Check("Read-only (no uploads/deletes)");
    private readonly Control _ftpCreds;
    private readonly Button _ftpToggle;
    // Syslog
    private readonly ComboBox _syslogBind;
    private readonly NumericUpDown _syslogPort = Ui.Number(514, 1, 65535);
    private readonly CheckBox _syslogToFile = Ui.Check("Also save to a log file");
    private readonly TextBox _syslogFile = Ui.Input(Path.Combine(AppPaths.DataDirectory, "logs", "syslog.txt"), mono: true);
    private readonly Button _syslogToggle;
    // SNTP
    private readonly ComboBox _sntpBind;
    private readonly Button _sntpToggle;
    // DHCP
    private readonly TextBox _dhcpStart = Ui.Input("10.10.10.100", mono: true), _dhcpEnd = Ui.Input("10.10.10.150", mono: true), _dhcpMask = Ui.Input("255.255.255.0", mono: true),
        _dhcpGateway = Ui.Input("10.10.10.1", mono: true), _dhcpDns = Ui.Input("10.10.10.1", mono: true);
    private readonly ComboBox _dhcpBind;
    private readonly Button _dhcpToggle;
    private readonly TextBlock _leases = Ui.Text("", 12, mono: true, color: Ui.Muted);

    public NetworkServicesTool(ToolContext ctx)
    {
        ComboBox Bind(bool allowAny = true)
        {
            var list = allowAny ? _addrs : _addrs.Where(a => a != "0.0.0.0").ToList();
            return Ui.Combo(list, list.Count > 0 ? 0 : -1);
        }
        _tftpBind = Bind(); _ftpBind = Bind(); _syslogBind = Bind(); _sntpBind = Bind(); _dhcpBind = Bind(false);
        _tftpToggle = Ui.Button("Start TFTP server", ToggleTftp, accent: true);
        _ftpToggle = Ui.Button("Start FTP server", ToggleFtp, accent: true);
        _syslogToggle = Ui.Button("Start Syslog server", ToggleSyslog, accent: true);
        _sntpToggle = Ui.Button("Start SNTP server", ToggleSntp, accent: true);
        _dhcpToggle = Ui.Button("Start DHCP server", ToggleDhcp, accent: true);
        _get = Ui.Button("Download (GET)", () => _ = RunClientAsync(get: true), accent: true);
        _put = Ui.Button("Upload (PUT)", () => _ = RunClientAsync(get: false));
        foreach (var b in new[] { _tftpToggle, _ftpToggle, _syslogToggle, _sntpToggle, _dhcpToggle }) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        _ftpCreds = Ui.Columns("*,*", Ui.Field("Username", _ftpUser), Ui.Field("Password", _ftpPass));
        _ftpCreds.IsVisible = false;
        _ftpAnon.IsCheckedChanged += (_, _) => _ftpCreds.IsVisible = _ftpAnon.IsChecked != true;
        var syslogFileRow = Ui.Columns("*,Auto", Ui.Field("Log file", _syslogFile),
            Ui.Button("Browse...", async () => { if (await ToolUi.PickSavePathAsync("syslog", ".txt", "Text") is { } p) _syslogFile.Text = p; }));
        syslogFileRow.IsVisible = false;
        _syslogToFile.IsCheckedChanged += (_, _) => syslogFileRow.IsVisible = _syslogToFile.IsChecked == true;

        var privileged = OperatingSystem.IsWindows() || Environment.UserName == "root" ? null : Ui.Text(
            "Ports below 1024 need extra permission on Linux/macOS: the .deb package grants it (cap_net_bind_service); otherwise pick a port above 1024 or run: sudo setcap cap_net_bind_service+ep " + Environment.ProcessPath,
            11.5, color: Ui.Amber);
        Control Note() => privileged is null ? new Panel() : Ui.Text(privileged.Text ?? "", 11.5, color: Ui.Amber);

        _panels =
        [
            Ui.Stack(10, Ui.Text("Serve files to devices: copy run tftp, IOS upgrades, firewall config restores.", 12.5, color: Ui.Muted),
                Ui.Columns("*,Auto", Ui.Field("Root folder", _tftpRoot), Ui.Button("Browse...", async () => { if (await ToolUi.PickFolderAsync() is { } d) _tftpRoot.Text = d; })),
                Ui.Columns("*,100", Ui.Field("Bind address", _tftpBind), Ui.Field("Port", _tftpPort)), _tftpWrite, _tftpOverwrite, _tftpToggle, Note()),
            Ui.Stack(10, Ui.Text("Fetch a file from (or send one to) another TFTP server.", 12.5, color: Ui.Muted),
                Ui.Columns("*,100", Ui.Field("Server host", _clientHost), Ui.Field("Port", _clientPort)),
                Ui.Field("Remote filename", _clientRemote),
                Ui.Columns("*,Auto", Ui.Field("Local file", _clientLocal), Ui.Button("Browse...", async () => { if (await ToolUi.PickSavePathAsync("download", ".bin", "Any file") is { } p) _clientLocal.Text = p; })),
                Ui.Row(_get, _put), _clientProgress, _clientStatus),
            Ui.Stack(10, Ui.Text("A small FTP server for devices that prefer FTP (passive mode).", 12.5, color: Ui.Muted),
                Ui.Columns("*,Auto", Ui.Field("Root folder", _ftpRoot), Ui.Button("Browse...", async () => { if (await ToolUi.PickFolderAsync() is { } d) _ftpRoot.Text = d; })),
                Ui.Columns("*,100", Ui.Field("Bind address", _ftpBind), Ui.Field("Port", _ftpPort)), _ftpAnon, _ftpCreds, _ftpReadOnly, _ftpToggle, Note()),
            Ui.Stack(10, Ui.Text("Collect syslog from devices (logging host <this IP>). Messages appear in the log below.", 12.5, color: Ui.Muted),
                Ui.Columns("*,100", Ui.Field("Bind address", _syslogBind), Ui.Field("Port", _syslogPort)), _syslogToFile, syslogFileRow, _syslogToggle, Note()),
            Ui.Stack(10, Ui.Text("Answer NTP/SNTP time requests on udp/123 with this computer's clock (ntp server <this IP>).", 12.5, color: Ui.Muted),
                Ui.Field("Bind address", _sntpBind), _sntpToggle, Note()),
            Ui.Stack(10, Ui.Text("Hand out addresses on an isolated lab or staging network. Never run it on a network that already has DHCP.", 12.5, color: Ui.Amber),
                Ui.Columns("*,*", Ui.Field("Pool start", _dhcpStart), Ui.Field("Pool end", _dhcpEnd)),
                Ui.Columns("*,*", Ui.Field("Subnet mask", _dhcpMask), Ui.Field("Gateway", _dhcpGateway)),
                Ui.Columns("*,*", Ui.Field("DNS server", _dhcpDns), Ui.Field("Bind / server IP", _dhcpBind)), _dhcpToggle, _leases, Note()),
        ];
        _tabs = new[] { "TFTP server", "TFTP client", "FTP server", "Syslog", "SNTP", "DHCP" }.Select((t, i) =>
        {
            var b = new ToggleButton { Content = t, IsChecked = i == 0 };
            b.Click += (_, _) => ShowPanel(i);
            return b;
        }).ToArray();

        var settings = Ui.Card(new ScrollViewer { Content = new Panel { Children = { _panels[0], _panels[1], _panels[2], _panels[3], _panels[4], _panels[5] } } });
        var logCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = {
                    WithDock(Ui.Row(_follow, Ui.Button("Clear", () => { _log.Clear(); _logBox.Text = ""; })), Dock.Right), Ui.Row(Ui.Section("Log"), _running) } }, Dock.Top),
                _logBox,
            },
        });
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("420,*"), ColumnSpacing = 16 };
        body.Children.Add(settings);
        Grid.SetColumn(logCard, 1);
        body.Children.Add(logCard);
        var ip = LocalAddresses.PrimaryIpv4();
        Content = Ui.Page("Network Services", "TFTP/FTP servers and client, Syslog, SNTP and DHCP - the everyday helper services for upgrades, backups and lab staging, all logged in one place.", body,
            new DockPanel { Children = { WithDock(Ui.Row(Ui.Text($"This computer: {ip}", 12.5, mono: true), Ui.Button("Copy IP", () => ToolUi.Copy(ip))), Dock.Right), Ui.Row(_tabs) } });
        ShowPanel(0);
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void ShowPanel(int i)
    {
        for (int p = 0; p < _panels.Length; p++) { _panels[p].IsVisible = p == i; _tabs[p].IsChecked = p == i; }
    }

    private void Wire(INetworkService svc)
    {
        svc.Logged += e => Dispatcher.UIThread.Post(() => Append($"[{svc.Name}] {e}"));
        svc.StateChanged += () => Dispatcher.UIThread.Post(UpdateRunning);
    }

    private void Append(string line)
    {
        _log.AddLast(line);
        while (_log.Count > MaxLogLines) _log.RemoveFirst();
        _logBox.Text = string.Join('\n', _log);
        // Caret at the start of the last line: scrolls down without scrolling sideways to a long line's end.
        if (_follow.IsChecked == true) _logBox.CaretIndex = _logBox.Text.Length - line.Length;
    }

    private void UpdateRunning()
    {
        var running = new[] { _tftp, _ftp, (INetworkService?)_syslog, _sntp, _dhcp }.Where(s => s?.IsRunning == true).Select(s => s!.Name);
        _running.Text = running.Any() ? "Running: " + string.Join(", ", running) : "No services running";
        _tftpToggle.Content = _tftp?.IsRunning == true ? "Stop TFTP server" : "Start TFTP server";
        _ftpToggle.Content = _ftp?.IsRunning == true ? "Stop FTP server" : "Start FTP server";
        _syslogToggle.Content = _syslog?.IsRunning == true ? "Stop Syslog server" : "Start Syslog server";
        _sntpToggle.Content = _sntp?.IsRunning == true ? "Stop SNTP server" : "Start SNTP server";
        _dhcpToggle.Content = _dhcp?.IsRunning == true ? "Stop DHCP server" : "Start DHCP server";
    }

    /// <summary>A refused low port becomes a clear instruction instead of "Permission denied".</summary>
    private void Fail(string service, Exception ex)
    {
        var msg = ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied } && !OperatingSystem.IsWindows()
            ? $"{ex.Message} - ports below 1024 need the cap_net_bind_service capability (granted by the .deb package), root, or a higher port."
            : ex.Message;
        Append($"[error] {service}: {msg}");
    }

    private string BindOf(ComboBox box) => box.SelectedItem as string ?? "0.0.0.0";

    private void ToggleTftp()
    {
        if (_tftp?.IsRunning == true) { _tftp.Stop(); return; }
        try
        {
            _tftp = new TftpServer((_tftpRoot.Text ?? "").Trim(), BindOf(_tftpBind), Ui.IntValue(_tftpPort, 69), _tftpWrite.IsChecked == true, _tftpOverwrite.IsChecked == true);
            Wire(_tftp);
            _tftp.Start();
        }
        catch (Exception ex) { Fail("TFTP", ex); _tftp = null; }
    }

    private async Task RunClientAsync(bool get)
    {
        var host = (_clientHost.Text ?? "").Trim();
        var remote = (_clientRemote.Text ?? "").Trim();
        var local = (_clientLocal.Text ?? "").Trim();
        if (host.Length == 0 || remote.Length == 0 || local.Length == 0) { _clientStatus.Text = "Fill in host, remote filename and local file."; return; }
        _clientCts = new CancellationTokenSource();
        _get.IsEnabled = _put.IsEnabled = false;
        _clientProgress.IsVisible = true;
        _clientStatus.Text = get ? $"Downloading {remote}..." : $"Uploading to {remote}...";
        try
        {
            var client = new TftpClient();
            int port = Ui.IntValue(_clientPort, 69);
            long last = 0;
            void Progress(long n) => Dispatcher.UIThread.Post(() => { last = n; _clientStatus.Text = $"{(get ? "Downloaded" : "Uploaded")} {n:N0} bytes..."; });
            if (get)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(local))!);
                await client.GetAsync(host, remote, local, port, Progress, _clientCts.Token);
                Append($"[TFTP client] GET {remote} from {host} -> {local} ({last:N0} bytes)");
            }
            else
            {
                await client.PutAsync(host, local, remote, port, Progress, _clientCts.Token);
                Append($"[TFTP client] PUT {local} -> {host}:{remote} ({last:N0} bytes)");
            }
            _clientStatus.Text = $"Done - {last:N0} bytes.";
        }
        catch (Exception ex) { _clientStatus.Text = $"Failed: {ex.Message}"; Append($"[TFTP client] error: {ex.Message}"); }
        finally
        {
            _clientProgress.IsVisible = false;
            _get.IsEnabled = _put.IsEnabled = true;
            _clientCts = null;
        }
    }

    private void ToggleFtp()
    {
        if (_ftp?.IsRunning == true) { _ftp.Stop(); return; }
        try
        {
            string? user = _ftpAnon.IsChecked == true ? null : (_ftpUser.Text ?? "").Trim();
            _ftp = new FtpServer((_ftpRoot.Text ?? "").Trim(), BindOf(_ftpBind), Ui.IntValue(_ftpPort, 21), user, user is null ? null : _ftpPass.Text, _ftpReadOnly.IsChecked == true);
            Wire(_ftp);
            _ftp.Start();
        }
        catch (Exception ex) { Fail("FTP", ex); _ftp = null; }
    }

    private void ToggleSyslog()
    {
        if (_syslog?.IsRunning == true) { _syslog.Stop(); return; }
        try
        {
            _syslog = new SyslogServer(BindOf(_syslogBind), Ui.IntValue(_syslogPort, 514), _syslogToFile.IsChecked == true ? (_syslogFile.Text ?? "").Trim() : null);
            Wire(_syslog);
            _syslog.Start();
        }
        catch (Exception ex) { Fail("Syslog", ex); _syslog = null; }
    }

    private void ToggleSntp()
    {
        if (_sntp?.IsRunning == true) { _sntp.Stop(); return; }
        try
        {
            _sntp = new SntpServer(BindOf(_sntpBind));
            Wire(_sntp);
            _sntp.Start();
        }
        catch (Exception ex) { Fail("SNTP", ex); _sntp = null; }
    }

    private void ToggleDhcp()
    {
        if (_dhcp?.IsRunning == true) { _dhcp.Stop(); _leaseTimer?.Stop(); return; }
        try
        {
            var bind = BindOf(_dhcpBind);
            _dhcp = new DhcpServer((_dhcpStart.Text ?? "").Trim(), (_dhcpEnd.Text ?? "").Trim(), (_dhcpMask.Text ?? "").Trim(),
                (_dhcpGateway.Text ?? "").Trim(), (_dhcpDns.Text ?? "").Trim(), bind, bindAddress: bind);
            Wire(_dhcp);
            _dhcp.Start();
            _leaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _leaseTimer.Tick += (_, _) =>
            {
                if (_dhcp is null) return;
                var leases = _dhcp.Leases;
                _leases.Text = leases.Count == 0 ? "No leases yet." : "Leases:\n" + string.Join('\n', leases.Select(l => $"  {l.Ip,-15} {l.Mac}  {l.HostName}".TrimEnd()));
            };
            _leaseTimer.Start();
        }
        catch (Exception ex) { Fail("DHCP", ex); _dhcp = null; }
    }

    public void Shutdown()
    {
        _clientCts?.Cancel();
        _leaseTimer?.Stop();
        _tftp?.Dispose();
        _ftp?.Dispose();
        _syslog?.Dispose();
        _sntp?.Dispose();
        _dhcp?.Dispose();
    }
}
