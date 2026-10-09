using System.Net;
using Avalonia.Controls;
using Avalonia.Layout;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Desktop.Tools;

public sealed class SubnetTool : UserControl, IToolView
{
    private readonly TextBox _input = Ui.Input("192.168.10.77/26", mono: true);
    private readonly Grid _results = new() { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,*"), RowSpacing = 10, ColumnSpacing = 24 };
    private readonly Border _errorBar;
    private readonly TextBlock _errorText;
    private readonly NumericUpDown _split = Ui.Number(28, 1, 32);
    private readonly TextBox _splitOut = Ui.Output(260), _rangeOut = Ui.Output(260), _summaryOut = Ui.Output(136);
    private readonly TextBox _range = Ui.Input("10.0.0.5-10.0.0.20", mono: true);
    private readonly TextBox _summaryIn = Ui.MultiInput("10.1.0.0/24\n10.1.1.0/24\n10.1.2.0/23", 120);
    private Subnet? _current;

    public SubnetTool()
    {
        _input.FontSize = 18;
        _errorBar = Ui.Banner(out _errorText, Ui.Rose);
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,14,*,14,*") };
        void Place(Control c, int col) { Grid.SetColumn(c, col); grid.Children.Add(c); }
        Place(Ui.Card(Ui.Stack(10, Ui.Section("Split into subnets"), Ui.Field("New prefix length", _split), _splitOut)), 0);
        Place(Ui.Card(Ui.Stack(10, Ui.Section("Range → CIDR"), Ui.Field("Start - end", _range), _rangeOut)), 2);
        Place(Ui.Card(Ui.Stack(10, Ui.Section("Summarise routes"), Ui.Field("Prefixes (one per line)", _summaryIn), _summaryOut)), 4);

        Content = Ui.ScrollPage("Subnet Calculator",
            "Accepts 10.1.2.3/24, 10.1.2.3 255.255.255.0, Cisco wildcard masks (0.0.0.255) and IPv6. Results update as you type.",
            Ui.Card(Ui.Stack(14, Ui.Field("Address / prefix", _input), _errorBar, _results)),
            grid);

        _input.TextChanged += (_, _) => Calculate();
        _split.ValueChanged += (_, _) => UpdateSplit();
        _range.TextChanged += (_, _) => UpdateRange();
        _summaryIn.TextChanged += (_, _) => UpdateSummary();
        Calculate();
        UpdateRange();
        UpdateSummary();
    }

    private void Calculate()
    {
        _results.Children.Clear();
        _results.RowDefinitions.Clear();
        var text = _input.Text ?? "";
        if (!Subnet.TryParse(text, out var s))
        {
            _current = null;
            Ui.Show(_errorBar, _errorText, text.Trim().Length > 0 ? "Not a valid address or prefix yet." : null);
            _splitOut.Text = "";
            return;
        }
        Ui.Show(_errorBar, _errorText, null);
        _current = s;

        var rows = new List<(string, string)>
        {
            ("Network", s.Cidr), ("Type", s.Classification), ("Address count", $"{s.TotalAddresses:N0}"),
            ("Usable hosts", $"{s.UsableHosts:N0}"), ("First host", s.FirstHost.ToString()), ("Last host", s.LastHost.ToString()),
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
            int r = i / 2, c = i % 2 * 2;
            if (_results.RowDefinitions.Count <= r) _results.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.Section(rows[i].Item1);
            label.VerticalAlignment = VerticalAlignment.Center;
            var value = Ui.Selectable(rows[i].Item2, 15, mono: true);
            Grid.SetRow(label, r); Grid.SetColumn(label, c);
            Grid.SetRow(value, r); Grid.SetColumn(value, c + 1);
            _results.Children.Add(label);
            _results.Children.Add(value);
        }
        UpdateSplit();
    }

    private static string ReverseZone(Subnet s)
    {
        int octets = Math.Clamp(s.PrefixLength / 8, 1, 4);
        return string.Join('.', s.Network.GetAddressBytes().Take(octets).Reverse()) + ".in-addr.arpa";
    }

    private void UpdateSplit()
    {
        if (_current is null || _current.IsV6) { _splitOut.Text = _current?.IsV6 == true ? "Splitting is IPv4 only." : ""; return; }
        int prefix = Ui.IntValue(_split, _current.PrefixLength);
        if (prefix < _current.PrefixLength) { _splitOut.Text = $"Pick a prefix of /{_current.PrefixLength} or longer."; return; }
        var parts = _current.Split(prefix, 1024);
        long total = 1L << (prefix - _current.PrefixLength);
        _splitOut.Text = $"{total:N0} × /{prefix} ({parts[0].UsableHosts:N0} hosts each)\n"
                         + string.Join('\n', parts.Select(p => $"{p.Cidr,-19} {p.FirstHost}-{p.LastHost}"))
                         + (total > parts.Count ? $"\n… {total - parts.Count:N0} more" : "");
    }

    private void UpdateRange()
    {
        var parts = (_range.Text ?? "").Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Ipv4.IsIpv4(parts[0]) || !Ipv4.IsIpv4(parts[1]))
        {
            _rangeOut.Text = "Enter an IPv4 range like 10.0.0.5-10.0.0.20";
            return;
        }
        var cidrs = Ipv4.RangeToCidrs(IPAddress.Parse(parts[0]), IPAddress.Parse(parts[1]));
        _rangeOut.Text = $"{cidrs.Count} prefix(es):\n" + string.Join('\n', cidrs.Select(c => c.Cidr));
    }

    private void UpdateSummary()
    {
        var subnets = new List<Subnet>();
        var bad = new List<string>();
        foreach (var line in (_summaryIn.Text ?? "").Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Subnet.TryParse(line, out var s) && !s.IsV6) subnets.Add(s); else bad.Add(line);
        }
        var merged = Ipv4.Summarize(subnets);
        _summaryOut.Text = $"{subnets.Count} in → {merged.Count} out\n" + string.Join('\n', merged.Select(m => m.Cidr))
                           + (bad.Count > 0 ? $"\nignored: {string.Join(", ", bad)}" : "");
    }

    public void Shutdown() { }
}
