using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Models;

namespace OmlTerminal.App.Views;

/// <summary>Live view-model for one node row: the same instance is reused and updated in place across polls (matched by Node.Id), so the list doesn't flicker or lose selection/scroll position on refresh.</summary>
public sealed class OmlLabNodeView(OmlLabNode node) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public OmlLabNode Node { get; private set; } = node;
    public string Id => Node.Id;

    public string Name => Node.Name;
    public bool Connectable => Node.Connectable;
    public string Subtitle => $"{Node.NodeType} · {Node.DeviceTemplate}";
    public string ConsoleLabel => Node.ConsoleType switch
    {
        OmlConsoleType.Telnet => "TELNET",
        OmlConsoleType.Vnc => "VNC",
        OmlConsoleType.Rdp => "RDP",
        OmlConsoleType.Ssh => "SSH",
        _ => "N/A",
    };

    public string Status => Node.IsRunning ? "running" : Node.Status;
    public double RowOpacity => Node.Connectable ? 1.0 : 0.45;

    // Beyond running/stopped, OML Labs can report transitional states like "starting"/"pending" or "error" -
    // distinguishing those from a plain stopped node matters when you're watching a lab boot up.
    public Brush StatusColor => (Brush)Application.Current.Resources[Node.Status.ToLowerInvariant() switch
    {
        "running" => "StatusConnectedBrush",
        "starting" or "pending" or "provisioning" or "booting" => "StatusConnectingBrush",
        "error" or "failed" or "crashed" => "StatusErrorBrush",
        _ => "StatusIdleBrush",
    }];

    /// <summary>Updates in place and raises PropertyChanged only for what can actually change between polls.</summary>
    public void Update(OmlLabNode latest)
    {
        Node = latest;
        Raise(nameof(Status));
        Raise(nameof(StatusColor));
        Raise(nameof(RowOpacity));
    }
}

public sealed partial class OmlLabTabView : UserControl
{
    public OmlLabLink Link { get; }

    private readonly OmlLabsClient _client;
    private readonly OmlHostCertStore _certStore = new();
    private readonly ObservableCollection<OmlLabNodeView> _nodes = new();
    private CancellationTokenSource? _pollCts;

    /// <summary>A node was double-clicked; the caller decides how to open it (and what to show if it isn't running yet).</summary>
    public event Action<OmlLabNode>? NodeActivated;

    /// <summary>"Connect All Online" was clicked, with every currently-ready node.</summary>
    public event Action<IReadOnlyList<OmlLabNode>>? ConnectAllRequested;

    public OmlLabTabView(OmlLabLink link)
    {
        InitializeComponent();
        Link = link;
        _client = new OmlLabsClient(_certStore);
        LabTitle.Text = link.LabName;
        LabHostText.Text = new Uri(link.OmlHost).Host;
        NodeList.ItemsSource = _nodes;

        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            await LoadAsync(showSpinner: first);
            first = false;
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task LoadAsync(bool showSpinner)
    {
        if (showSpinner) Busy.IsActive = true;
        try
        {
            var nodes = await _client.GetNodesAsync(Link);
            Reconcile(nodes);
            ErrorBar.IsOpen = false;
            TrustPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{nodes.Count} node(s) · {nodes.Count(n => n.IsRunning)} running · updated {DateTime.Now:HH:mm:ss}";
            ConnectAllButton.IsEnabled = nodes.Any(n => n.IsReadyToConnect);
        }
        catch (OmlUntrustedCertificateException ex)
        {
            StatusText.Text = "Waiting for certificate confirmation...";
            TrustTitle.Text = $"OML Labs at {ex.Host} presented a certificate this app hasn't seen before.";
            TrustDetail.Text = $"SHA-256: {ex.Thumbprint}\nSubject: {ex.Subject}";
            TrustPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // A live-polled tab shouldn't nag on every transient failure (e.g. an expired token) - show it once, quietly.
            ErrorBar.Message = $"{ex.Message} (the launch link may have expired - open a fresh one from OML Labs)";
            ErrorBar.IsOpen = true;
        }
        finally { Busy.IsActive = false; }
    }

    private void Reconcile(IReadOnlyList<OmlLabNode> latest)
    {
        var byId = latest.ToDictionary(n => n.Id);
        for (int i = _nodes.Count - 1; i >= 0; i--)
            if (!byId.ContainsKey(_nodes[i].Id)) _nodes.RemoveAt(i);

        var existingIds = _nodes.Select(v => v.Id).ToHashSet();
        foreach (var n in latest)
        {
            if (existingIds.Contains(n.Id)) _nodes.First(v => v.Id == n.Id).Update(n);
            else _nodes.Add(new OmlLabNodeView(n));
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(showSpinner: true);

    private async void TrustAndRetry_Click(object sender, RoutedEventArgs e)
    {
        var host = new Uri(Link.OmlHost).Host;
        var thumbprint = TrustDetail.Text.Split('\n')[0].Replace("SHA-256: ", "");
        _certStore.Trust(host, thumbprint);
        await LoadAsync(showSpinner: true);
    }

    private void TrustCancel_Click(object sender, RoutedEventArgs e) => TrustPanel.Visibility = Visibility.Collapsed;

    private void NodeList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not OmlLabNodeView view || !view.Connectable) return;
        NodeActivated?.Invoke(view.Node);
    }

    private void ConnectAll_Click(object sender, RoutedEventArgs e)
    {
        var ready = _nodes.Where(v => v.Node.IsReadyToConnect).Select(v => v.Node).ToList();
        if (ready.Count > 0) ConnectAllRequested?.Invoke(ready);
    }

    public void Detach()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        _client.Dispose();
    }
}
