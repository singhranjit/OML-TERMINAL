using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.App.Views.Tools;
using OmlTerminal.Core.Models;

namespace OmlTerminal.App.Views;

public sealed partial class HomeView : UserControl
{
    public event Action<string>? QuickConnectRequested;
    public event Action<SessionProfile>? SessionChosen;
    public event Action? OmlNodeRequested;
    public event Action<string>? ToolRequested;
    public event Action<ProtocolKind>? NewSessionRequested;

    private static readonly (string Label, string Glyph, ProtocolKind Kind)[] Tiles =
    [
        ("SSH", "", ProtocolKind.Ssh),
        ("Telnet", "", ProtocolKind.Telnet),
        ("Serial", "", ProtocolKind.Serial),
        ("RDP", "", ProtocolKind.Rdp),
        ("VNC", "", ProtocolKind.Vnc),
        ("SFTP", "", ProtocolKind.Sftp),
        ("Shell", "", ProtocolKind.Local),
    ];

    public HomeView()
    {
        InitializeComponent();
        ToolGrid.ItemsSource = ToolCatalog.All;
        foreach (var (label, glyph, kind) in Tiles) SessionTiles.Items.Add(Tile(label, glyph, kind));
    }

    private Button Tile(string label, string glyph, ProtocolKind kind)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 24, Foreground = (Brush)Application.Current.Resources["OmlSkyBrush"] });
        panel.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var b = new Button
        {
            Content = panel,
            Width = 108,
            Height = 86,
            CornerRadius = (CornerRadius)Application.Current.Resources["CardRadius"],
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
        };
        ToolTipService.SetToolTip(b, $"New {label} session");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"New {label} session");
        b.Click += (_, _) => NewSessionRequested?.Invoke(kind);
        return b;
    }

    private void OmlNode_Click(object sender, RoutedEventArgs e) => OmlNodeRequested?.Invoke();

    private void ToolGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolDescriptor d) ToolRequested?.Invoke(d.Id);
    }

    public void SetRecent(IEnumerable<SessionProfile> recent)
    {
        var list = recent.ToList();
        RecentList.ItemsSource = list;
        NoRecentText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QuickConnect_Click(object sender, RoutedEventArgs e) => Submit();

    private void QuickBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Submit(); }
    }

    private void Submit()
    {
        var text = QuickBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        QuickBox.Text = "";
        QuickConnectRequested?.Invoke(text);
    }

    private void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionProfile p) SessionChosen?.Invoke(p);
    }
}
