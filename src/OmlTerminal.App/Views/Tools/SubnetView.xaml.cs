using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class SubnetView : UserControl, IToolView
{
    private Subnet? _current;

    public SubnetView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Calculate(); RangeBox_TextChanged(this, null!); SummaryInput_TextChanged(this, null!); };
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => Calculate();

    private void Calculate()
    {
        if (ResultGrid is null) return;
        ResultGrid.Children.Clear();
        ResultGrid.RowDefinitions.Clear();
        if (!Subnet.TryParse(InputBox.Text, out var s))
        {
            _current = null;
            ErrorBar.Message = "Not a valid address or prefix yet.";
            ErrorBar.IsOpen = InputBox.Text.Trim().Length > 0;
            SplitOutput.Text = "";
            return;
        }
        ErrorBar.IsOpen = false;
        _current = s;

        var rows = new List<(string, string)>
        {
            ("Network", s.Cidr),
            ("Type", s.Classification),
            ("Address count", $"{s.TotalAddresses:N0}"),
            ("Usable hosts", $"{s.UsableHosts:N0}"),
            ("First host", s.FirstHost.ToString()),
            ("Last host", s.LastHost.ToString()),
        };
        if (!s.IsV6)
        {
            rows.Add(("Subnet mask", s.SubnetMask.ToString()));
            rows.Add(("Wildcard (ACL)", s.Wildcard.ToString()));
            rows.Add(("Broadcast", s.Broadcast.ToString()));
            rows.Add(("Binary mask", string.Join('.', s.SubnetMask.GetAddressBytes().Select(b => Convert.ToString(b, 2).PadLeft(8, '0')))));
            rows.Add(("Hex network", "0x" + Convert.ToHexString(s.Network.GetAddressBytes())));
            rows.Add(("Reverse zone", ReverseZone(s)));
        }
        else
        {
            rows.Add(("Last address", s.LastAddress.ToString()));
            rows.Add(("Full network", string.Join(':', Enumerable.Range(0, 8).Select(i => Convert.ToHexString(s.Network.GetAddressBytes(), i * 2, 2).ToLowerInvariant()))));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            int r = i / 2, c = (i % 2) * 2;
            if (ResultGrid.RowDefinitions.Count <= r) ResultGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Item1.ToUpperInvariant(), Style = (Style)Application.Current.Resources["SectionLabelStyle"], VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock
            {
                Text = rows[i].Item2, FontSize = 15, IsTextSelectionEnabled = true,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFont"],
            };
            Grid.SetRow(label, r); Grid.SetColumn(label, c);
            Grid.SetRow(value, r); Grid.SetColumn(value, c + 1);
            ResultGrid.Children.Add(label);
            ResultGrid.Children.Add(value);
        }
        UpdateSplit();
    }

    /// <summary>The in-addr.arpa zone on the enclosing octet boundary (a /26 lives in its /24's zone).</summary>
    private static string ReverseZone(Subnet s)
    {
        int octets = Math.Clamp(s.PrefixLength / 8, 1, 4);
        return string.Join('.', s.Network.GetAddressBytes().Take(octets).Reverse()) + ".in-addr.arpa";
    }

    private void SplitBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateSplit();

    private void UpdateSplit()
    {
        if (SplitOutput is null) return;
        if (_current is null || _current.IsV6) { SplitOutput.Text = _current?.IsV6 == true ? "Splitting is IPv4 only." : ""; return; }
        int prefix = double.IsNaN(SplitBox.Value) ? _current.PrefixLength : (int)SplitBox.Value;
        if (prefix < _current.PrefixLength) { SplitOutput.Text = $"Pick a prefix of /{_current.PrefixLength} or longer."; return; }
        var parts = _current.Split(prefix, 1024);
        long total = 1L << (prefix - _current.PrefixLength);
        var lines = parts.Select(p => $"{p.Cidr,-19} {p.FirstHost}-{p.LastHost}");
        SplitOutput.Text = $"{total:N0} × /{prefix} ({parts[0].UsableHosts:N0} hosts each)\n" + string.Join('\n', lines)
                           + (total > parts.Count ? $"\n… {total - parts.Count:N0} more" : "");
    }

    private void RangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RangeOutput is null) return;
        var parts = RangeBox.Text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Ipv4.IsIpv4(parts[0]) || !Ipv4.IsIpv4(parts[1]))
        {
            RangeOutput.Text = "Enter an IPv4 range like 10.0.0.5-10.0.0.20";
            return;
        }
        var cidrs = Ipv4.RangeToCidrs(IPAddress.Parse(parts[0]), IPAddress.Parse(parts[1]));
        RangeOutput.Text = $"{cidrs.Count} prefix(es):\n" + string.Join('\n', cidrs.Select(c => c.Cidr));
    }

    private void SummaryInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SummaryOutput is null) return;
        var subnets = new List<Subnet>();
        var bad = new List<string>();
        foreach (var line in SummaryInput.Text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Subnet.TryParse(line, out var s) && !s.IsV6) subnets.Add(s); else bad.Add(line);
        }
        var merged = Ipv4.Summarize(subnets);
        SummaryOutput.Text = $"{subnets.Count} in → {merged.Count} out\n" + string.Join('\n', merged.Select(m => m.Cidr))
                             + (bad.Count > 0 ? $"\nignored: {string.Join(", ", bad)}" : "");
    }

    public void Shutdown() { }
}
