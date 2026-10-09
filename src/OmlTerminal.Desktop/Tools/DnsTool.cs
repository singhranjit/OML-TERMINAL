using System.Net;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OmlTerminal.Core.NetTools;

namespace OmlTerminal.Desktop.Tools;

public sealed class DnsRow(string section, DnsRecord? r, string? note = null)
{
    public string Section { get; } = section;
    public string Name => r?.Name ?? "";
    public string Type => r?.Type.ToString() ?? "";
    public string Ttl => r is null ? "" : r.Ttl.ToString();
    public string Data => note ?? r?.Data ?? "";
    public IBrush SectionBrush => Section switch { "ANSWER" => Ui.Mint, "AUTHORITY" => Ui.Violet, "ADDITIONAL" => Ui.Sky, _ => Ui.Orange };
}

public sealed class DnsTool : UserControl, IToolView
{
    private const string SystemLabel = "System resolver";
    private static readonly DnsRecordType[] Types = [DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.CNAME, DnsRecordType.MX, DnsRecordType.NS,
        DnsRecordType.TXT, DnsRecordType.SOA, DnsRecordType.SRV, DnsRecordType.PTR, DnsRecordType.CAA];

    private readonly TextBox _name = Ui.Input("", "example.com", mono: true);
    private readonly ComboBox _type = Ui.Combo(Types.Select(t => t.ToString()).ToList());
    private readonly AutoCompleteBox _server = new() { ItemsSource = new[] { SystemLabel, "1.1.1.1", "8.8.8.8", "9.9.9.9", "208.67.222.222" }, Text = SystemLabel, FilterMode = AutoCompleteFilterMode.None, MinimumPrefixLength = 0 };
    private readonly DataGrid _results;
    private readonly TextBlock _status = Ui.Text("", 12, mono: true, color: Ui.Muted);

    public DnsTool()
    {
        _results = Ui.Table(
            Ui.Col<DnsRow>("Section", r => r.Section, 110, r => r.SectionBrush),
            Ui.Col<DnsRow>("Name", r => r.Name, 240, mono: true),
            Ui.Col<DnsRow>("Type", r => r.Type, 70),
            Ui.Col<DnsRow>("TTL", r => r.Ttl, 80, sortKey: r => int.TryParse(r.Ttl, out var t) ? t : -1),
            Ui.Col<DnsRow>("Data", r => r.Data, mono: true, fill: true));
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = QueryAsync(); } };
        var fill = new DockPanel { Children = { _status, _results } };
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Avalonia.Thickness(0, 8, 0, 0);
        Content = Ui.Page("DNS Lookup", "dig-style queries against your system resolver or any server. Compare asks four resolvers at once.", fill,
            Ui.Columns("*,120,220,Auto,Auto",
                Ui.Field("Name or IP", _name), Ui.Field("Type", _type), Ui.Field("Resolver", _server),
                Ui.Button("Query", () => _ = QueryAsync(), accent: true),
                Ui.Button("Compare", () => _ = CompareAsync(), tip: "Ask the system resolver, Cloudflare, Google and Quad9")));
    }

    private DnsRecordType SelectedType => Types[Math.Max(0, _type.SelectedIndex)];

    private IPAddress? SelectedServer()
    {
        var text = (_server.Text ?? "").Trim();
        if (text.Length == 0 || text == SystemLabel) return null;
        return IPAddress.TryParse(text, out var ip) ? ip : throw new FormatException($"'{text}' is not a resolver IP address.");
    }

    private async Task QueryAsync()
    {
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) return;
        // An IP with the default A type almost always means "who is this?"
        if (IPAddress.TryParse(name, out _) && SelectedType is DnsRecordType.A or DnsRecordType.AAAA) _type.SelectedIndex = Array.IndexOf(Types, DnsRecordType.PTR);
        try
        {
            _status.Text = "Querying...";
            var r = await DnsLookup.QueryAsync(name, SelectedType, SelectedServer());
            var rows = r.Records.Select(x => new DnsRow(x.Section, x)).ToList();
            if (rows.Count == 0) rows.Add(new DnsRow(r.ResponseCode, null, r.ResponseCode == "NXDOMAIN" ? "Name does not exist." : "No records of this type."));
            _results.ItemsSource = rows;
            _status.Text = $"{r.ResponseCode} from {r.Server} in {r.Elapsed.TotalMilliseconds:0} ms" +
                           $"{(r.Authoritative ? " · authoritative" : "")}{(r.Truncated ? " · truncated (retried over TCP)" : "")} · {r.Records.Count} record(s)";
        }
        catch (Exception ex) { _status.Text = ex.Message; _results.ItemsSource = null; }
    }

    private async Task CompareAsync()
    {
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) return;
        var servers = new[] { DnsLookup.SystemResolver(), IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("9.9.9.9") };
        _status.Text = "Asking 4 resolvers...";
        var type = SelectedType;
        var rows = await Task.WhenAll(servers.Select(async s =>
        {
            try
            {
                var r = await DnsLookup.QueryAsync(name, type, s);
                var answers = r.Records.Where(x => x.Section == "ANSWER").Select(x => x.Data).OrderBy(x => x).ToList();
                return new DnsRow($"@{s}", null, answers.Count > 0 ? string.Join("  ·  ", answers) + $"   ({r.Elapsed.TotalMilliseconds:0} ms)" : r.ResponseCode);
            }
            catch (Exception ex) { return new DnsRow($"@{s}", null, ex.Message); }
        }));
        _results.ItemsSource = rows.ToList();
        var distinct = rows.Select(r => r.Data.Split("   (")[0]).Distinct().Count();
        _status.Text = distinct == 1 ? "All resolvers agree." : $"Resolvers disagree ({distinct} different answers) - check split DNS, caching or propagation.";
    }

    public void Shutdown() { }
}
