using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.ChangeGuard;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Parsing;
using OmlTerminal.Core.Search;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Desktop.Tools;

public sealed class GlobalSearchTool : UserControl, IToolView
{
    private const int MaxResults = 5000;
    private readonly ToolContext _ctx;
    private CancellationTokenSource? _cts;
    private string? _previewPath;
    private string[] _previewLines = [];

    private readonly TextBox _query = Ui.Input("", "10.20.30.40   ·   10.20.30.0/24   ·   0050.56a1.0001   ·   any text", mono: true);
    private readonly TextBlock _kind = Ui.Text("", 12, color: Ui.Sky);
    private readonly CheckBox _sessions = Ui.Check("Sessions", true), _backups = Ui.Check("Config backups", true), _allVersions = Ui.Check("older backup versions too"),
        _logs = Ui.Check("Session logs", true), _snapshots = Ui.Check("Change Guard captures", true);
    private readonly ListBox _results = new() { Background = Brushes.Transparent };
    private readonly TextBlock _status = Ui.Section("Results");
    private readonly TextBlock _previewTitle = Ui.Section("Preview");
    private readonly TextBox _preview = Ui.Output();
    private readonly Button _search, _cancel, _openSession, _showFile;

    public GlobalSearchTool(ToolContext ctx)
    {
        _ctx = ctx;
        _query.FontSize = 15;
        foreach (var c in new Control[] { _kind, _sessions, _backups, _allVersions, _logs, _snapshots })
        {
            c.Margin = new Thickness(0, 0, 14, 4);
            c.VerticalAlignment = VerticalAlignment.Center;
        }
        _search = Ui.Button("Search", () => _ = SearchAsync(), accent: true);
        _cancel = Ui.Button("Stop", () => _cts?.Cancel());
        _cancel.IsEnabled = false;
        _openSession = Ui.Button("Connect", OpenSession, tip: "Connect to this device");
        _showFile = Ui.Button("Show file", () => { if (_results.SelectedItem is SearchHit { FilePath: { } p }) ToolUi.OpenInFileManager(p); });
        _openSession.IsEnabled = _showFile.IsEnabled = false;
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); } };
        _query.TextChanged += (_, _) => ShowKind();
        _results.ItemTemplate = new FuncDataTemplate<SearchHit>((h, _) =>
        {
            if (h is null) return new Panel();
            var kind = h.Kind switch { SearchSourceKind.Session => "SESSION", SearchSourceKind.Backup => "BACKUP", SearchSourceKind.Snapshot => "CAPTURE", _ => "LOG" };
            var matchBrush = h.Match is "exact" or "MAC" or "text" ? Ui.Mint : Ui.Amber;
            return Ui.Stack(2,
                Ui.Row(new Border { BorderBrush = Ui.Violet, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0),
                        Child = new TextBlock { Text = kind, FontSize = 10, Foreground = Ui.Violet } },
                    new TextBlock { Text = h.LineNumber > 0 ? $"{h.Source}  ·  line {h.LineNumber}" : h.Source, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = h.Match, FontSize = 11, Foreground = matchBrush }),
                new TextBlock { Text = h.Line, FontFamily = Ui.Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = h.Context, FontSize = 11, Foreground = Ui.Muted, IsVisible = !string.IsNullOrEmpty(h.Context), TextTrimming = TextTrimming.CharacterEllipsis });
        });
        _results.SelectionChanged += (_, _) => ShowPreview();

        var resultsCard = Ui.Card(new DockPanel { Children = { WithDock(_status, Dock.Top), new ScrollViewer { Content = _results, Margin = new Thickness(0, 8, 0, 0) } } });
        var previewCard = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(new DockPanel { Children = { WithDock(Ui.Row(_openSession, _showFile, Ui.Button("Copy line", () => { if (_results.SelectedItem is SearchHit h) ToolUi.Copy(h.Line); })), Dock.Right), _previewTitle } }, Dock.Top),
                _preview,
            },
        });
        _preview.Margin = new Thickness(0, 8, 0, 0);
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,*"), ColumnSpacing = 16 };
        body.Children.Add(resultsCard);
        Grid.SetColumn(previewCard, 1);
        body.Children.Add(previewCard);
        Content = Ui.Page("Global Search", "Find an IP, subnet, MAC address or any text across every config backup, session log, Change Guard capture and saved session.", body,
            Ui.Columns("*,Auto,Auto", _query, _search, _cancel),
            new WrapPanel { Children = { _kind, _sessions, _backups, _allVersions, _logs, _snapshots } });
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { _query.Focus(); _query.SelectAll(); });
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    public void FocusSearch() => Dispatcher.UIThread.Post(() => { _query.Focus(); _query.SelectAll(); });

    private void ShowKind()
    {
        var t = (_query.Text ?? "").Trim();
        if (t.Length == 0) { _kind.Text = ""; return; }
        var q = SearchQuery.Parse(t);
        _kind.Text = q.Kind switch
        {
            QueryKind.Ipv4 => "IP address · plus subnets/ranges containing it",
            QueryKind.Network => $"Network {q.Network!.Cidr} · inside or overlapping",
            QueryKind.Mac => "MAC address · any notation",
            _ => "Text · case-insensitive",
        };
    }

    private async Task SearchAsync()
    {
        if (_cts is not null) return;
        var text = (_query.Text ?? "").Trim();
        if (text.Length < 2) { _status.Text = "TYPE AT LEAST TWO CHARACTERS"; return; }
        var query = SearchQuery.Parse(text);
        var hits = new List<SearchHit>();
        if (_sessions.IsChecked == true) hits.AddRange(GlobalSearch.SearchSessions(_ctx.Model.Sessions.ToList(), query));

        var s = _ctx.Settings;
        var roots = new List<string> { s.BackupDirectory, ConfigBackup.DefaultDirectory, Path.Combine(Core.Persistence.AppPaths.DataDirectory, "migration-pulls") };
        roots.AddRange(s.ScheduledBackups.Select(j => j.RootDirectory).Where(r => r.Length > 0));
        bool backups = _backups.IsChecked == true, all = _allVersions.IsChecked == true, snaps = _snapshots.IsChecked == true, logs = _logs.IsChecked == true;
        var logDir = s.LogDirectory;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _search.IsEnabled = false;
        _cancel.IsEnabled = true;
        _results.ItemsSource = null;
        _preview.Text = "";
        _status.Text = "SEARCHING…";
        var sw = Stopwatch.StartNew();
        int fileCount = 0;
        bool cancelled = false;
        try
        {
            hits.AddRange(await Task.Run(() =>
            {
                var files = new List<SearchFile>();
                if (backups) files.AddRange(GlobalSearch.BackupFiles(roots, all));
                if (snaps) files.AddRange(GlobalSearch.SnapshotFiles(GuardStore.DefaultRoot));
                if (logs) files.AddRange(GlobalSearch.LogFiles(logDir));
                fileCount = files.Count;
                var bag = new ConcurrentBag<(int Order, SearchHit Hit)>();
                int total = 0;
                Parallel.ForEach(files.Select((f, i) => (File: f, Index: i)),
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                    (x, state) =>
                    {
                        foreach (var h in GlobalSearch.SearchFile(x.File, query))
                        {
                            bag.Add((x.Index, h));
                            if (Interlocked.Increment(ref total) >= MaxResults) { state.Stop(); break; }
                        }
                    });
                return bag.OrderBy(b => b.Order).ThenBy(b => b.Hit.LineNumber).Select(b => b.Hit).ToList();
            }, ct));
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _search.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
        var ordered = hits.OrderBy(h => GlobalSearch.Relevance(h.Match))
            .ThenBy(h => h.Kind switch { SearchSourceKind.Session => 0, SearchSourceKind.Backup => 1, SearchSourceKind.Snapshot => 2, _ => 3 })
            .Take(MaxResults).ToList();
        _results.ItemsSource = ordered;
        _status.Text = $"{ordered.Count:N0}{(ordered.Count >= MaxResults ? "+" : "")} RESULT{(ordered.Count == 1 ? "" : "S")} · {fileCount:N0} FILES · {sw.Elapsed.TotalSeconds:0.0}s{(cancelled ? " · STOPPED" : "")}";
        if (ordered.Count > 0) _results.SelectedIndex = 0;
        else _preview.Text = fileCount == 0 && hits.Count == 0
            ? "Nothing to search yet - take some config backups, turn on session logging, or run Change Guard."
            : "No matches.";
    }

    private void ShowPreview()
    {
        var hit = _results.SelectedItem as SearchHit;
        _openSession.IsEnabled = hit is not null && SessionFor(hit) is not null;
        _showFile.IsEnabled = hit?.FilePath is not null;
        if (hit is null) { _previewTitle.Text = "PREVIEW"; _preview.Text = ""; return; }
        if (hit.FilePath is null)
        {
            _previewTitle.Text = "SESSION";
            var s = _ctx.Model.Sessions.FirstOrDefault(x => x.Display == hit.Source);
            _preview.Text = s is null ? hit.Line : string.Join("\n", new[]
            {
                $"Name      {s.Name}", $"Folder    {s.Folder}", $"Protocol  {s.ProtocolLabel}", $"Host      {s.Host}:{s.Port}",
                $"Username  {s.Username}", $"Tags      {s.Tags}", $"Notes     {s.Notes}",
            });
            return;
        }
        _previewTitle.Text = Path.GetFileName(hit.FilePath).ToUpperInvariant();
        try
        {
            if (_previewPath != hit.FilePath) { _previewLines = GlobalSearch.ReadAllLinesShared(hit.FilePath); _previewPath = hit.FilePath; }
            int from = Math.Max(0, hit.LineNumber - 1 - 14), to = Math.Min(_previewLines.Length, hit.LineNumber - 1 + 15);
            int width = to.ToString().Length;
            var sb = new StringBuilder();
            if (hit.Context.Length > 0) sb.AppendLine($"[{hit.Context}]").AppendLine();
            for (int i = from; i < to; i++)
                sb.AppendLine($"{(i == hit.LineNumber - 1 ? "▶" : " ")} {(i + 1).ToString().PadLeft(width)}  {_previewLines[i]}");
            _preview.Text = sb.ToString();
        }
        catch (Exception ex) { _preview.Text = ex.Message; }
    }

    /// <summary>The saved session a hit belongs to: the session itself, or the device a backup folder / capture is named after.</summary>
    private SessionProfile? SessionFor(SearchHit hit)
    {
        var sessions = _ctx.Sessions();
        return hit.Kind switch
        {
            SearchSourceKind.Session => sessions.FirstOrDefault(s => s.Display == hit.Source),
            SearchSourceKind.Backup => sessions.FirstOrDefault(s => s.IsSshBased && ConfigBackup.SafeFileName(string.IsNullOrWhiteSpace(s.Name) ? s.Host : s.Name) == hit.Source),
            SearchSourceKind.Snapshot when hit.Source.IndexOf(" › ", StringComparison.Ordinal) is > 0 and var i && hit.Source.LastIndexOf(" (", StringComparison.Ordinal) is > 0 and var j && j > i
                => sessions.FirstOrDefault(s => s.Name == hit.Source[(i + 3)..j]),
            _ => null,
        };
    }

    private void OpenSession()
    {
        if (_results.SelectedItem is SearchHit h && SessionFor(h) is { } s) _ctx.OpenSession(s);
    }

    public void Shutdown() => _cts?.Cancel();
}

public sealed class StructuredOutputTool : UserControl, IToolView
{
    private static readonly string[] StateColumns = ["Status", "Protocol", "Link", "Admin", "State", "State/PfxRcd"];
    private static readonly string[] BadWords = ["down", "err-disabled", "notconnect", "idle", "init", "exstart", "absent"];

    private readonly ToolContext _ctx;
    private ShowTable? _table;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _parseDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private readonly ComboBox _device = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = new FuncDataTemplate<SessionProfile>((p, _) => new TextBlock { Text = p?.Display }) };
    private readonly TextBox _command = Ui.Input("show ip interface brief", mono: true);
    private readonly TextBox _input = Ui.MultiInput("", double.NaN, "Paste show-command output here, grab it from the terminal, or run a command on a device.");
    private readonly TextBlock _source = Ui.Text("", 12, color: Ui.Muted);
    private readonly TextBlock _title = Ui.Section("No table yet"), _info = Ui.Text("", 12, color: Ui.Muted);
    private readonly TextBox _filter = Ui.Input("", "Filter rows");
    private readonly Panel _tableHost = new();
    private readonly TextBlock _empty = Ui.Text("Column-aligned output with a header line works best: show ip interface brief, show interfaces status, show vlan, show ip bgp summary, show cdp neighbors, show mac address-table...", 13, color: Ui.Muted);
    private readonly Button _run;
    private DataGrid? _grid;

    public StructuredOutputTool(ToolContext ctx)
    {
        _ctx = ctx;
        _run = Ui.Button("Run", () => _ = RunAsync());
        _command.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = RunAsync(); } };
        _parseDelay.Tick += (_, _) => { _parseDelay.Stop(); ParseInput(); };
        _input.TextChanged += (_, _) => { _parseDelay.Stop(); _parseDelay.Start(); };
        _filter.TextChanged += (_, _) => Render();
        _tableHost.Children.Add(_empty);

        var left = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(10,
                    Ui.Section("Source"),
                    Ui.Row(Ui.Button("Grab from terminal", Grab, accent: true), Ui.Button("Paste", () => _ = PasteAsync())),
                    Ui.Field("Or run on a saved SSH session", _device),
                    Ui.Columns("*,Auto", _command, _run)), Dock.Top),
                WithDock(_source, Dock.Bottom),
                _input,
            },
        });
        _input.Margin = new Thickness(0, 10);
        var right = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(8,
                    Ui.Stack(1, _title, _info),
                    new DockPanel
                    {
                        Margin = new Thickness(0, 0, 0, 8),
                        Children =
                        {
                            WithDock(Ui.Row(Ui.Button("Copy CSV", () => Export(t => t.ToCsv(), "Copied as CSV.")), Ui.Button("Copy Markdown", () => Export(t => t.ToMarkdown(), "Copied as a Markdown table.")),
                                Ui.Button("Save CSV...", () => _ = SaveAsync())), Dock.Right),
                            _filter,
                        },
                    }), Dock.Top),
                _tableHost,
            },
        });
        _filter.Margin = new Thickness(0, 0, 8, 0);
        _filter.VerticalAlignment = VerticalAlignment.Center;
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("380,*"), ColumnSpacing = 16 };
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Content = Ui.Page("Structured Output", "Turn show-command output into a sortable, filterable table - problems in red, export as CSV or Markdown.", body);
        AttachedToVisualTree += (_, _) => ToolUi.FillSessions(_device, _ctx.SshSessions());
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private void Grab()
    {
        var buffer = _ctx.ActiveTerminalText();
        if (string.IsNullOrWhiteSpace(buffer)) { _source.Text = "No terminal tab is open - connect to a device and run a command first."; return; }
        var (command, output) = ShowTableParser.LastCommandOutput(buffer);
        _input.Text = output;
        _source.Text = command is null ? "Took the whole terminal buffer (no CLI prompt found)." : $"From the terminal: {command}";
        ParseInput();
    }

    private async Task PasteAsync()
    {
        if (MainWindow.Current?.Clipboard is not { } clip) return;
        try
        {
            var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clip);
            if (string.IsNullOrEmpty(text)) { _source.Text = "The clipboard has no text."; return; }
            _input.Text = text;
            _source.Text = "Pasted from the clipboard.";
            ParseInput();
        }
        catch (Exception ex) { _source.Text = ex.Message; }
    }

    /// <summary>Runs the command on its own side-channel SSH connection - never in a terminal tab someone is using.</summary>
    private async Task RunAsync()
    {
        if (_cts is not null) return;
        if (_device.SelectedItem is not SessionProfile device) { _source.Text = "Pick a saved SSH session first."; return; }
        var command = (_command.Text ?? "").Trim();
        if (command.Length == 0) { _source.Text = "Type a command to run."; return; }
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _run.IsEnabled = false;
        _source.Text = $"Running '{command}' on {device.Name}...";
        try
        {
            var ct = _cts.Token;
            var output = await Task.Run(async () =>
            {
                using var session = await DeviceSession.OpenAsync(device, BackupMode.Shell, ["terminal length 0"], ct);
                return await session.RunAsync(command, ct);
            });
            _input.Text = output;
            _source.Text = $"{device.Name}: {command}";
            ParseInput();
        }
        catch (OperationCanceledException) { _source.Text = "Timed out."; }
        catch (Exception ex) { _source.Text = $"{device.Name}: {ex.Message}"; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _run.IsEnabled = true;
        }
    }

    private void ParseInput()
    {
        _parseDelay.Stop();
        _table = string.IsNullOrWhiteSpace(_input.Text) ? null : ShowTableParser.Parse(_input.Text!);
        _tableHost.Children.Clear();
        if (_table is null)
        {
            _grid = null;
            _tableHost.Children.Add(_empty);
            _title.Text = string.IsNullOrWhiteSpace(_input.Text) ? "NO TABLE YET" : "NOT RECOGNISED";
            _info.Text = string.IsNullOrWhiteSpace(_input.Text) ? "" : "No table found in this output.";
            return;
        }
        var stateCols = StateColumns.Select(_table.ColumnIndex).Where(i => i >= 0).ToList();
        var columns = _table.Columns.Select((name, i) => Ui.Col<IReadOnlyList<string>>(name, r => i < r.Count ? r[i] : "",
            mono: true, color: r => IsProblem(r, stateCols) ? Ui.Rose : null, sortKey: r => new NaturalKey(i < r.Count ? r[i] : ""))).ToArray();
        _grid = Ui.Table(columns);
        _tableHost.Children.Add(_grid);
        Render();
    }

    /// <summary>Wraps a cell so DataGrid sorting uses the natural order (Gi1/0/2 before Gi1/0/10).</summary>
    private sealed record NaturalKey(string Value) : IComparable
    {
        public int CompareTo(object? obj) => NaturalComparer.Instance.Compare(Value, (obj as NaturalKey)?.Value);
    }

    private IReadOnlyList<IReadOnlyList<string>> VisibleRows()
    {
        if (_table is null) return [];
        var q = (_filter.Text ?? "").Trim();
        return q.Length == 0 ? _table.Rows : _table.Rows.Where(r => r.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void Render()
    {
        if (_table is null || _grid is null) return;
        var rows = VisibleRows();
        _title.Text = _table.Title.ToUpperInvariant();
        _info.Text = rows.Count == _table.Rows.Count ? $"{_table.Rows.Count:N0} rows · {_table.Columns.Count} columns" : $"{rows.Count:N0} of {_table.Rows.Count:N0} rows";
        _grid.ItemsSource = rows;
    }

    /// <summary>Rows worth a second look in red: something down/err-disabled, or a BGP session that isn't Established.</summary>
    private bool IsProblem(IReadOnlyList<string> row, List<int> stateCols)
    {
        foreach (var i in stateCols)
        {
            if (i >= row.Count) continue;
            var v = row[i].ToLowerInvariant();
            if (v.Length == 0) continue;
            if (_table!.Columns[i] == "State/PfxRcd") { if (!v.All(char.IsDigit)) return true; continue; }
            if (v == "connected" || v.StartsWith("full") || v.StartsWith("2way") || v is "active" or "standby" or "up") continue;
            if (BadWords.Any(v.Contains)) return true;
        }
        return false;
    }

    private IReadOnlyList<IReadOnlyList<string>> ExportRows()
    {
        var selected = _grid?.SelectedItems.OfType<IReadOnlyList<string>>().ToList() ?? [];
        return selected.Count > 1 ? selected : VisibleRows();
    }

    private void Export(Func<ShowTable, string> format, string done)
    {
        if (_table is null) return;
        ToolUi.Copy(format(_table with { Rows = ExportRows() }));
        _info.Text = done;
    }

    private async Task SaveAsync()
    {
        if (_table is null) return;
        var name = $"{_table.Title.ToLowerInvariant().Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmm}";
        if (await ToolUi.SaveTextAsync(name, (_table with { Rows = ExportRows() }).ToCsv(), ".csv", "CSV") is { } path)
            _info.Text = $"Saved {Path.GetFileName(path)}";
    }

    public void Shutdown() => _cts?.Cancel();
}
