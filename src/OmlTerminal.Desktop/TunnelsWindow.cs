using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using OmlTerminal.Core.Transports;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>Local, remote and dynamic (SOCKS) port forwards riding on a connected SSH session.</summary>
public sealed class TunnelsWindow : Window
{
    private static readonly TunnelKind[] Kinds = [TunnelKind.Local, TunnelKind.Remote, TunnelKind.Dynamic];

    private readonly ISshTunnelHost _host;
    private readonly ItemsControl _list = new();
    private readonly TextBlock _empty = Ui.Text("No tunnels yet.", 13, color: Ui.Muted);
    private readonly ComboBox _kind = Ui.Combo(new[]
    {
        "Local  (a port on this computer → a target reachable from the remote)",
        "Remote  (a port on the remote → a target reachable from this computer)",
        "Dynamic  (SOCKS proxy on this computer)",
    });
    private readonly TextBox _bindHost = Ui.Input("127.0.0.1", mono: true), _targetHost = Ui.Input("", "10.0.0.5", mono: true);
    private readonly NumericUpDown _bindPort = Ui.Number(0, 0, 65535), _targetPort = Ui.Number(22, 1, 65535);
    private readonly Control _targetRow;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;

    public TunnelsWindow(ISshTunnelHost host, string sessionName)
    {
        Title = $"SSH Tunnels - {sessionName}";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _host = host;
        _banner = Ui.Banner(out _bannerText, Ui.Rose);
        _list.ItemTemplate = new FuncDataTemplate<TunnelSpec>((t, _) =>
        {
            if (t is null) return new Panel();
            var remove = Ui.Button("Remove", () => { _host.StopTunnel(t.Id); Refresh(); });
            var row = new DockPanel { Margin = new Thickness(0, 2) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = t.Display, FontFamily = Ui.Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            return row;
        });
        _kind.SelectionChanged += (_, _) => _targetRow!.IsVisible = Kind != TunnelKind.Dynamic;
        _targetRow = Ui.Columns("*,120", Ui.Field("Target host", _targetHost), Ui.Field("Target port", _targetPort));

        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                Ui.Text("Port forwards riding on this session's SSH connection. The remote server must allow TCP forwarding (most Linux/Unix jump hosts do; most network device SSH servers don't).", 12.5, color: Ui.Muted),
                Ui.Section("Active tunnels"), _list, _empty,
                Ui.Section("Add tunnel"),
                Ui.Field("Type", _kind),
                Ui.Columns("*,120", Ui.Field("Bind address", _bindHost), Ui.Field("Bind port (0 = auto)", _bindPort)),
                _targetRow,
                _banner,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { Ui.Button("Close", Close), Ui.Button("Add tunnel", Add, accent: true) } },
            },
        };
        ((TextBlock)((StackPanel)Content).Children[0]).TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        host.TunnelFailed += OnTunnelFailed;
        Closed += (_, _) => host.TunnelFailed -= OnTunnelFailed;
        Refresh();
    }

    private TunnelKind Kind => Kinds[Math.Max(0, _kind.SelectedIndex)];

    private void OnTunnelFailed(TunnelSpec spec, string error) => Dispatcher.UIThread.Post(() =>
    {
        Ui.Show(_banner, _bannerText, $"{spec.Display}: {error}");
        Refresh();
    });

    private void Refresh()
    {
        var tunnels = _host.ActiveTunnels.ToList();
        _list.ItemsSource = tunnels;
        _empty.IsVisible = tunnels.Count == 0;
    }

    private void Add()
    {
        Ui.Show(_banner, _bannerText, null);
        var spec = new TunnelSpec
        {
            Kind = Kind,
            BoundHost = string.IsNullOrWhiteSpace(_bindHost.Text) ? "127.0.0.1" : _bindHost.Text.Trim(),
            BoundPort = Ui.IntValue(_bindPort, 0),
            TargetHost = (_targetHost.Text ?? "").Trim(),
            TargetPort = Ui.IntValue(_targetPort, 0),
        };
        var errors = spec.Validate();
        if (errors.Count > 0) { Ui.Show(_banner, _bannerText, string.Join(" ", errors)); return; }
        try
        {
            _host.StartTunnel(spec);
            _targetHost.Text = "";
            Refresh();
        }
        catch (Exception ex) { Ui.Show(_banner, _bannerText, ex.Message); }
    }
}
