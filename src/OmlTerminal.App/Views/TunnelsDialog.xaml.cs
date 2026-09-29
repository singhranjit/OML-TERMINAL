using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.App.Views;

public sealed partial class TunnelsDialog : ContentDialog
{
    private readonly ISshTunnelHost _host;

    public TunnelsDialog(XamlRoot xamlRoot, ISshTunnelHost host)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        _host = host;
        KindBox.SelectedIndex = 0;
        host.TunnelFailed += OnTunnelFailed;
        Closed += (_, _) => host.TunnelFailed -= OnTunnelFailed;
        Refresh();
    }

    private void OnTunnelFailed(TunnelSpec spec, string error) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            ErrorBar.Message = $"{spec.Display}: {error}";
            ErrorBar.IsOpen = true;
            Refresh();
        });

    private void Refresh()
    {
        var tunnels = _host.ActiveTunnels;
        TunnelList.ItemsSource = tunnels;
        EmptyText.Visibility = tunnels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Guid id) _host.StopTunnel(id);
        Refresh();
    }

    private static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedItem is null || TargetRow is null) return;
        TargetRow.Visibility = TagOf(KindBox) == nameof(TunnelKind.Dynamic) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        var spec = new TunnelSpec
        {
            Kind = Enum.Parse<TunnelKind>(TagOf(KindBox)),
            BoundHost = string.IsNullOrWhiteSpace(BoundHostBox.Text) ? "127.0.0.1" : BoundHostBox.Text.Trim(),
            BoundPort = double.IsNaN(BoundPortBox.Value) ? 0 : (int)BoundPortBox.Value,
            TargetHost = TargetHostBox.Text.Trim(),
            TargetPort = double.IsNaN(TargetPortBox.Value) ? 0 : (int)TargetPortBox.Value,
        };
        var errors = spec.Validate();
        if (errors.Count > 0)
        {
            ErrorBar.Message = string.Join(" ", errors);
            ErrorBar.IsOpen = true;
            return;
        }
        try
        {
            _host.StartTunnel(spec);
            TargetHostBox.Text = "";
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }
}
