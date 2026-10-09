using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using OmlTerminal.App.ViewModels;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Desktop.Tools;

/// <summary>A tool that lives in its own tab. Shutdown() is called when the tab closes - cancel scans, stop captures.</summary>
public interface IToolView
{
    void Shutdown();
}

/// <summary>What a tool may ask of the main window, without holding a reference to it (same shape as the Windows app's).</summary>
public sealed class ToolContext
{
    /// <summary>Saved sessions with their password-manager credentials already filled in - ready to connect with.</summary>
    public required Func<IReadOnlyList<SessionProfile>> Sessions { get; init; }
    public required MainViewModel Model { get; init; }
    /// <summary>Types text into the active terminal tab and presses Enter; false when there's no connected terminal.</summary>
    public required Func<string, bool> SendToActiveSession { get; init; }
    /// <summary>Types text into the active terminal without pressing Enter.</summary>
    public required Func<string, bool> InsertIntoActiveSession { get; init; }
    public required AppSettings Settings { get; init; }
    public required Action SaveSettings { get; init; }
    public required Action<SessionProfile> OpenSession { get; init; }
    public Func<string?> ActiveTerminalText { get; init; } = () => null;
    /// <summary>Opens (or focuses) another tool's tab, e.g. "trace this host" from a scan result.</summary>
    public Action<string, Action<Control>?> OpenTool { get; init; } = (_, _) => { };

    public IReadOnlyList<SessionProfile> SshSessions() =>
        Sessions().Where(s => s.IsSshBased).OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record ToolDescriptor(string Id, string Title, string Subtitle, string Category, Func<ToolContext, Control> Create);

public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDescriptor> All =
    [
        new("portscan", "Port Query", "TCP/UDP port check & scan with banner grab", "Network", _ => new PortScanTool()),
        new("localports", "Local Ports", "Listening & established sockets with owning process", "Network", _ => new LocalPortsTool()),
        new("ping", "Ping · Trace · Sweep", "ICMP ping, traceroute and subnet sweep", "Network", c => new PingTraceTool(c)),
        new("pathtrace", "Visual Trace", "Live traceroute drawn as a path with a plain-English verdict, hop-by-hop path through your devices (WireWalk), and multicast trees", "Network", c => new PathTraceTool(c)),
        new("pingmon", "Ping Monitor", "Continuous ping to many hosts at once - latency, loss, jitter, MOS and an outage log", "Network", c => new PingMonitorTool(c)),
        new("mrtg", "Traffic Graphs (MRTG)", "SNMP v1/v2c/v3 interface traffic - daily, weekly, monthly and yearly graphs with 95th percentile and alerts", "Network", c => new TrafficGraphTool(c)),
        new("dns", "DNS Lookup", "dig-style queries against any resolver", "Network", _ => new DnsTool()),
        new("subnet", "Subnet Calculator", "CIDR math, splitting, range → CIDR, summarise", "Network", _ => new SubnetTool()),
        new("backup", "Config Backup", "Pull running configs over SSH, detect changes, diff", "Security", c => new ConfigBackupTool(c)),
        new("scheduledbackups", "Scheduled Backups", "Recurring config backups with drift alerts, while the app is open", "Security", c => new ScheduledBackupsTool(c)),
    ];

    public static ToolDescriptor? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public static readonly string[] Categories = ["Network", "Security", "AI", "Reference", "Servers"];
}

/// <summary>Clipboard, file pickers and "show in folder" for tool views.</summary>
public static class ToolUi
{
    private static TopLevel? Top => MainWindow.Current;

    /// <summary>(Re)fills a session picker without losing what was picked.</summary>
    public static void FillSessions(ComboBox box, IReadOnlyList<SessionProfile> sessions, bool selectFirst = false)
    {
        var keep = (box.SelectedItem as SessionProfile)?.Id;
        box.ItemsSource = sessions;
        var again = keep is null ? null : sessions.FirstOrDefault(s => s.Id == keep);
        if (again is not null) box.SelectedItem = again;
        else if (selectFirst && sessions.Count > 0) box.SelectedIndex = 0;
    }

    public static async Task CopyAsync(string text)
    {
        if (Top?.Clipboard is { } c) { try { await c.SetTextAsync(text); } catch { } }
    }

    public static void Copy(string text) => _ = CopyAsync(text);

    public static async Task<string?> SaveTextAsync(string suggestedName, string content, string extension = ".txt", string typeName = "Text")
    {
        var path = await PickSavePathAsync(suggestedName, extension, typeName);
        if (path is null) return null;
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public static async Task<string?> PickSavePathAsync(string suggestedName, string extension, string typeName)
    {
        if (Top is not { } top) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = ["*" + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public static async Task<string?> PickOpenPathAsync(string title, params (string Name, string[] Patterns)[] types)
    {
        if (Top is not { } top) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types.Length == 0 ? null : types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns }).ToList(),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickFolderAsync()
    {
        if (Top is not { } top) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>Opens the folder (or the folder containing a file) in the desktop's file manager.</summary>
    public static void OpenInFileManager(string path)
    {
        try
        {
            var folder = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            var psi = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", File.Exists(path) ? ["-R", path] : [folder])
                : new ProcessStartInfo("xdg-open", [folder]);
            psi.UseShellExecute = false;
            Process.Start(psi)?.Dispose();
        }
        catch { }
    }

    public static string Csv(params string[] fields) =>
        string.Join(",", fields.Select(f => f.Contains(',') || f.Contains('"') || f.Contains('\n') ? $"\"{f.Replace("\"", "\"\"")}\"" : f));
}
