using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Services;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class NetworkServicesView : UserControl, IToolView
{
    private const int MaxLogLines = 2000;
    private readonly LinkedList<string> _log = new();
    private readonly ToolContext _ctx;

    private TftpServer? _tftp;
    private FtpServer? _ftp;
    private SyslogServer? _syslog;
    private SntpServer? _sntp;
    private DhcpServer? _dhcp;
    private CancellationTokenSource? _clientCts;
    private DispatcherTimer? _leaseTimer;

    private readonly StackPanel[] _panels;

    public NetworkServicesView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _panels = [TftpPanel, TftpClientPanel, FtpPanel, SyslogPanel, SntpPanel, DhcpPanel];

        var addrs = LocalAddresses.Ipv4();
        foreach (var box in new[] { TftpBind, FtpBind, SyslogBind, SntpBind })
            foreach (var a in addrs) box.Items.Add(a);
        foreach (var a in addrs.Where(a => a != "0.0.0.0")) DhcpBind.Items.Add(a);
        foreach (var box in new[] { TftpBind, FtpBind, SyslogBind, SntpBind }) box.SelectedIndex = 0;
        DhcpBind.SelectedIndex = 0;

        var defaultRoot = Path.Combine(AppPaths.DataDirectory, "tftp-root");
        TftpRoot.Text = defaultRoot;
        FtpRoot.Text = defaultRoot;
        ClientLocal.Text = Path.Combine(AppPaths.DataDirectory, "downloads");
        SyslogFile.Text = Path.Combine(AppPaths.DataDirectory, "logs", "syslog.txt");
        PrimaryIpText.Text = LocalAddresses.PrimaryIpv4();
    }

    private void Service_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int i = sender.Items.IndexOf(sender.SelectedItem);
        for (int p = 0; p < _panels.Length; p++) _panels[p].Visibility = p == i ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- shared log

    private void Wire(INetworkService svc)
    {
        svc.Logged += e => DispatcherQueue.TryEnqueue(() => Append($"[{svc.Name}] {e}"));
        svc.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateRunning);
    }

    private void Append(string line)
    {
        _log.AddLast(line);
        while (_log.Count > MaxLogLines) _log.RemoveFirst();
        LogBox.Text = string.Join('\n', _log);
        if (Follow.IsChecked == true) LogBox.SelectionStart = LogBox.Text.Length;
    }

    private void UpdateRunning()
    {
        var running = new[] { _tftp, _ftp, (INetworkService?)_syslog, _sntp, _dhcp }.Where(s => s?.IsRunning == true).Select(s => s!.Name);
        RunningText.Text = running.Any() ? "Running: " + string.Join(", ", running) : "No services running";
        TftpToggle.Content = _tftp?.IsRunning == true ? "Stop TFTP server" : "Start TFTP server";
        FtpToggle.Content = _ftp?.IsRunning == true ? "Stop FTP server" : "Start FTP server";
        SyslogToggle.Content = _syslog?.IsRunning == true ? "Stop Syslog server" : "Start Syslog server";
        SntpToggle.Content = _sntp?.IsRunning == true ? "Stop SNTP server" : "Start SNTP server";
        DhcpToggle.Content = _dhcp?.IsRunning == true ? "Stop DHCP server" : "Start DHCP server";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) { _log.Clear(); LogBox.Text = ""; }
    private void CopyIp_Click(object sender, RoutedEventArgs e) => ToolUi.Copy(PrimaryIpText.Text);

    private void Fail(string message) => Append($"[error] {message}");

    // ---------- TFTP server

    private void TftpToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_tftp?.IsRunning == true) { _tftp.Stop(); return; }
        try
        {
            _tftp = new TftpServer(TftpRoot.Text.Trim(), (string)TftpBind.SelectedItem, (int)TftpPort.Value,
                TftpAllowWrite.IsChecked == true, TftpOverwrite.IsChecked == true);
            Wire(_tftp);
            _tftp.Start();
        }
        catch (Exception ex) { Fail($"TFTP: {ex.Message}"); _tftp = null; }
    }

    private async void BrowseTftpRoot_Click(object sender, RoutedEventArgs e)
    {
        if (await ToolUi.PickFolderAsync() is { } d) TftpRoot.Text = d;
    }

    // ---------- TFTP client

    private async void BrowseClientLocal_Click(object sender, RoutedEventArgs e)
    {
        if (await ToolUi.PickSavePathAsync("download", ".bin", "Any file") is { } p) ClientLocal.Text = p;
    }

    private async void ClientGet_Click(object sender, RoutedEventArgs e) => await RunClient(get: true);
    private async void ClientPut_Click(object sender, RoutedEventArgs e) => await RunClient(get: false);

    private async Task RunClient(bool get)
    {
        var host = ClientHost.Text.Trim();
        var remote = ClientRemote.Text.Trim();
        var local = ClientLocal.Text.Trim();
        if (host.Length == 0 || remote.Length == 0 || local.Length == 0) { ClientStatus.Text = "Fill in host, remote filename and local file."; return; }

        _clientCts = new CancellationTokenSource();
        ClientGet.IsEnabled = ClientPut.IsEnabled = false;
        ClientProgress.Visibility = Visibility.Visible;
        ClientProgress.IsIndeterminate = true;
        ClientStatus.Text = get ? $"Downloading {remote}..." : $"Uploading to {remote}...";
        try
        {
            var client = new TftpClient();
            int port = (int)ClientPort.Value;
            long last = 0;
            void Progress(long n) => DispatcherQueue.TryEnqueue(() => { last = n; ClientStatus.Text = $"{(get ? "Downloaded" : "Uploaded")} {n:N0} bytes..."; });
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
            ClientStatus.Text = $"Done - {last:N0} bytes.";
        }
        catch (Exception ex) { ClientStatus.Text = $"Failed: {ex.Message}"; Append($"[TFTP client] error: {ex.Message}"); }
        finally
        {
            ClientProgress.Visibility = Visibility.Collapsed;
            ClientGet.IsEnabled = ClientPut.IsEnabled = true;
            _clientCts = null;
        }
    }

    // ---------- FTP

    private void FtpAnon_Click(object sender, RoutedEventArgs e) =>
        FtpCreds.Visibility = FtpAnon.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

    private void FtpToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_ftp?.IsRunning == true) { _ftp.Stop(); return; }
        try
        {
            string? user = FtpAnon.IsChecked == true ? null : FtpUser.Text.Trim();
            _ftp = new FtpServer(FtpRoot.Text.Trim(), (string)FtpBind.SelectedItem, (int)FtpPort.Value,
                user, user is null ? null : FtpPass.Text, FtpReadOnly.IsChecked == true);
            Wire(_ftp);
            _ftp.Start();
        }
        catch (Exception ex) { Fail($"FTP: {ex.Message}"); _ftp = null; }
    }

    private async void BrowseFtpRoot_Click(object sender, RoutedEventArgs e)
    {
        if (await ToolUi.PickFolderAsync() is { } d) FtpRoot.Text = d;
    }

    // ---------- Syslog

    private void SyslogToFile_Click(object sender, RoutedEventArgs e) =>
        SyslogFileRow.Visibility = SyslogToFile.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void SyslogToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syslog?.IsRunning == true) { _syslog.Stop(); return; }
        try
        {
            _syslog = new SyslogServer((string)SyslogBind.SelectedItem, (int)SyslogPort.Value,
                SyslogToFile.IsChecked == true ? SyslogFile.Text.Trim() : null);
            Wire(_syslog);
            _syslog.Start();
        }
        catch (Exception ex) { Fail($"Syslog: {ex.Message}"); _syslog = null; }
    }

    private async void BrowseSyslogFile_Click(object sender, RoutedEventArgs e)
    {
        if (await ToolUi.PickSavePathAsync("syslog", ".txt", "Text") is { } p) SyslogFile.Text = p;
    }

    // ---------- SNTP

    private void SntpToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_sntp?.IsRunning == true) { _sntp.Stop(); return; }
        try
        {
            _sntp = new SntpServer((string)SntpBind.SelectedItem);
            Wire(_sntp);
            _sntp.Start();
        }
        catch (Exception ex) { Fail($"SNTP: {ex.Message}"); _sntp = null; }
    }

    // ---------- DHCP

    private void DhcpToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_dhcp?.IsRunning == true) { _dhcp.Stop(); _leaseTimer?.Stop(); return; }
        try
        {
            var bind = (string)DhcpBind.SelectedItem;
            _dhcp = new DhcpServer(DhcpStart.Text.Trim(), DhcpEnd.Text.Trim(), DhcpMask.Text.Trim(),
                DhcpGateway.Text.Trim(), DhcpDns.Text.Trim(), bind, bindAddress: bind);
            Wire(_dhcp);
            _dhcp.Start();
            _leaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _leaseTimer.Tick += (_, _) => ShowLeases();
            _leaseTimer.Start();
        }
        catch (Exception ex) { Fail($"DHCP: {ex.Message}"); _dhcp = null; }
    }

    private void ShowLeases()
    {
        if (_dhcp is null) return;
        var leases = _dhcp.Leases;
        DhcpLeases.Text = leases.Count == 0 ? "No leases yet."
            : "Leases:\n" + string.Join('\n', leases.Select(l => $"  {l.Ip,-15} {l.Mac}  {l.HostName}".TrimEnd()));
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
