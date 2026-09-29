using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.App.Views.Tools;

public sealed class DnsRow(string section, DnsRecord? r, string? note = null)
{
    public string Section { get; } = section;
    public string Name => r?.Name ?? "";
    public string Type => r?.Type.ToString() ?? "";
    public string Ttl => r is null ? "" : r.Ttl.ToString();
    public string Data => note ?? r?.Data ?? "";
    public Brush SectionBrush => ToolUi.Brush(Section switch
    {
        "ANSWER" => "OmlMintBrush",
        "AUTHORITY" => "OmlVioletBrush",
        "ADDITIONAL" => "OmlSkyBrush",
        _ => "OmlOrangeBrush",
    });
}

public sealed partial class DnsView : UserControl, IToolView
{
    private const string SystemLabel = "System resolver";

    public DnsView()
    {
        InitializeComponent();
        foreach (var t in new[] { DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.CNAME, DnsRecordType.MX, DnsRecordType.NS,
                     DnsRecordType.TXT, DnsRecordType.SOA, DnsRecordType.SRV, DnsRecordType.PTR, DnsRecordType.CAA })
            TypeBox.Items.Add(t.ToString());
        TypeBox.SelectedIndex = 0;
        foreach (var s in new[] { SystemLabel, "1.1.1.1", "8.8.8.8", "9.9.9.9", "208.67.222.222" }) ServerBox.Items.Add(s);
        ServerBox.SelectedIndex = 0;
    }

    private void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Query_Click(sender, e); }
    }

    private DnsRecordType SelectedType => Enum.Parse<DnsRecordType>((string)TypeBox.SelectedItem);

    private IPAddress? SelectedServer()
    {
        var text = (ServerBox.Text is { Length: > 0 } t ? t : ServerBox.SelectedItem as string ?? "").Trim();
        if (text.Length == 0 || text == SystemLabel) return null;
        return IPAddress.TryParse(text, out var ip) ? ip : throw new FormatException($"'{text}' is not a resolver IP address.");
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        // Typing an IP with the default A type almost always means "who is this?"
        if (IPAddress.TryParse(name, out _) && SelectedType is DnsRecordType.A or DnsRecordType.AAAA) TypeBox.SelectedItem = "PTR";
        try
        {
            StatusText.Text = "Querying...";
            var r = await DnsLookup.QueryAsync(name, SelectedType, SelectedServer());
            Results.ItemsSource = Rows(r);
            StatusText.Text = $"{r.ResponseCode} from {r.Server} in {r.Elapsed.TotalMilliseconds:0} ms" +
                              $"{(r.Authoritative ? " · authoritative" : "")}{(r.Truncated ? " · truncated (retried over TCP)" : "")} · {r.Records.Count} record(s)";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; Results.ItemsSource = null; }
    }

    private static List<DnsRow> Rows(DnsResponse r)
    {
        var rows = r.Records.Select(x => new DnsRow(x.Section, x)).ToList();
        if (rows.Count == 0) rows.Add(new DnsRow(r.ResponseCode, null, r.ResponseCode == "NXDOMAIN" ? "Name does not exist." : "No records of this type."));
        return rows;
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        var servers = new[] { DnsLookup.SystemResolver(), IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("9.9.9.9") };
        StatusText.Text = "Asking 4 resolvers...";
        var type = SelectedType;
        var tasks = servers.Select(async s =>
        {
            try
            {
                var r = await DnsLookup.QueryAsync(name, type, s);
                var answers = r.Records.Where(x => x.Section == "ANSWER").Select(x => x.Data).OrderBy(x => x).ToList();
                return new DnsRow($"@{s}", null, answers.Count > 0 ? string.Join("  ·  ", answers) + $"   ({r.Elapsed.TotalMilliseconds:0} ms)" : r.ResponseCode);
            }
            catch (Exception ex) { return new DnsRow($"@{s}", null, ex.Message); }
        });
        var rows = await Task.WhenAll(tasks);
        Results.ItemsSource = rows.ToList();
        var distinct = rows.Select(r => r.Data.Split("   (")[0]).Distinct().Count();
        StatusText.Text = distinct == 1 ? "All resolvers agree." : $"Resolvers disagree ({distinct} different answers) - check split DNS, caching or propagation.";
    }

    public void Shutdown() { }
}
