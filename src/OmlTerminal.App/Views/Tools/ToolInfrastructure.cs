using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace OmlTerminal.App.Views.Tools;

/// <summary>A tool that lives in its own tab. Shutdown() is called when the tab closes - cancel scans, stop captures.</summary>
public interface IToolView
{
    void Shutdown();
}

/// <summary>What a tool may ask of the main window, without holding a reference to it.</summary>
public sealed class ToolContext
{
    /// <summary>Saved sessions with their password-manager credentials already filled in - ready to connect with.</summary>
    public required Func<IReadOnlyList<SessionProfile>> Sessions { get; init; }
    /// <summary>The app's model - the password manager edits the vault and session links through it.</summary>
    public required ViewModels.MainViewModel Model { get; init; }
    /// <summary>Types text into the active terminal tab; returns false when there's no connected terminal.</summary>
    public required Func<string, bool> SendToActiveSession { get; init; }
    /// <summary>Types text into the active terminal without pressing Enter, so the user can review/edit before running.
    /// Returns false when there's no connected terminal.</summary>
    public required Func<string, bool> InsertIntoActiveSession { get; init; }
    public required AppSettings Settings { get; init; }
    public required Action SaveSettings { get; init; }
    /// <summary>Opens a new terminal tab running the given profile (used by "SSH to this host" style actions).</summary>
    public required Action<SessionProfile> OpenSession { get; init; }
    /// <summary>Scrollback text of the active terminal tab (or the last one used), or null when there isn't one.</summary>
    public Func<string?> ActiveTerminalText { get; init; } = () => null;

    public IReadOnlyList<SessionProfile> SshSessions() => Sessions().Where(s => s.IsSshBased).OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record ToolDescriptor(string Id, string Title, string Subtitle, string Glyph, string Category, Func<ToolContext, UserControl> Create);

public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDescriptor> All =
    [
        new("portscan", "Port Query", "TCP/UDP port check & scan with banner grab", "", "Network", c => new PortScanView()),
        new("localports", "Local Ports", "Listening & established sockets with owning process", "", "Network", c => new LocalPortsView()),
        new("ping", "Ping · Trace · Sweep", "ICMP ping, traceroute and subnet sweep", "", "Network", c => new PingTraceView()),
        new("pathtrace", "Visual Trace", "Live traceroute drawn as a path with a plain-English verdict, hop-by-hop path through your devices, and multicast trees", "", "Network", c => new PathTraceView(c)),
        new("pingmon", "Ping Monitor", "Continuous ping to many hosts at once - latency, loss, jitter, MOS and an outage log", "", "Network", c => new PingMonitorView(c)),
        new("mrtg", "Traffic Graphs (MRTG)", "SNMP v1/v2c/v3 interface traffic - daily, weekly, monthly and yearly graphs with 95th percentile and alerts", "", "Network", c => new TrafficGraphView(c)),
        new("dns", "DNS Lookup", "dig-style queries against any resolver", "", "Network", c => new DnsView()),
        new("subnet", "Subnet Calculator", "CIDR math, splitting, range → CIDR, summarise", "", "Network", c => new SubnetView()),
        new("capture", "Packet Capture", "tcpdump / FortiGate sniffer / tshark → pcap", "", "Security", c => new CaptureView(c)),
        new("hostmonitor", "Host Monitor", "Live load, memory, disk and uptime for saved SSH sessions", "", "Network", c => new HostMonitorView(c)),
        new("topology", "Topology Mapper", "Discover the network from one device over CDP/LLDP and draw a live, clickable map", "", "Network", c => new TopologyView(c)),
        new("analyzer", "Packet Analyzer", "Capture on this PC's Ethernet or Wi-Fi (or open a pcap) - decode, filter, and get problems pointed out", "", "Network", c => new PacketAnalyzerView(c)),
        new("wifi", "Wi-Fi Analyzer", "Nearby networks, channel planning, roaming log and floor-plan site-survey heatmaps", "", "Network", c => new WifiAnalyzerView(c)),
        new("tables", "Structured Output", "Turn show-command output into a sortable, filterable table - CSV/Markdown export", "", "Network", c => new StructuredOutputView(c)),
        new("copilot", "AI Copilot", "Local-LLM assistant with its own connection to a device - approves risky commands with you", "", "AI", c => new CopilotView(c)),
        new("scripts", "Scripts", "Run your own PowerShell/Python/Bash scripts as external processes, with session context", "", "Reference", c => new ScriptsView(c)),
        new("fwobjects", "Firewall Object Builder", "Bulk addresses, FQDNs & services for 8 firewall vendors", "", "Security", c => new FirewallBuilderView(c)),
        new("vault", "Password Manager", "Saved logins & enable passwords, shared by sessions and backups", "", "Security", c => new PasswordManagerView(c)),
        new("fwpolicy", "Firewall Policy Builder", "Excel sheet of rules → policies for 8 firewall vendors", "", "Security", c => new FirewallPolicyView(c)),
        new("migration", "Config Migration", "Move objects, services and rules between Cisco ASA/FTD, FortiGate, Palo Alto, Juniper SRX and pfSense", "", "Security", c => new MigrationView(c)),
        new("backup", "Config Backup", "Pull running configs over SSH, detect changes, diff", "", "Security", c => new ConfigBackupView(c)),
        new("scheduledbackups", "Scheduled Backups", "Recurring config backups with drift alerts, while the app is open", "", "Security", c => new ScheduledBackupsView(c)),
        new("changeguard", "Change Guard", "Pre/post change checks: what went down, rerouted or disappeared, in plain English", "", "Security", c => new ChangeGuardView(c)),
        new("search", "Global Search", "Find an IP, subnet, MAC or text across every backup, log, capture and session (Ctrl+Shift+G)", "", "Reference", c => new GlobalSearchView(c)),
        new("cliguide", "CLI Guide", "Searchable command reference for every supported vendor", "", "Reference", c => new CliGuideView(c)),
        new("netservices", "Network Services", "TFTP/FTP servers & client, Syslog, SNTP, DHCP (tftpd64-style)", "", "Servers", c => new NetworkServicesView(c)),
    ];

    public static ToolDescriptor? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}

public static class ToolUi
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public static async Task<string?> SaveTextAsync(string suggestedName, string content, string extension = ".txt", string typeName = "Text")
    {
        var path = await PickSavePathAsync(suggestedName, extension, typeName);
        if (path is null) return null;
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public static async Task<string?> PickSavePathAsync(string suggestedName, string extension, string typeName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            var psi = File.Exists(path)
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"");
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    public static string Csv(params string[] fields) =>
        string.Join(",", fields.Select(f => f.Contains(',') || f.Contains('"') || f.Contains('\n') ? $"\"{f.Replace("\"", "\"\"")}\"" : f));
}
