using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.ChangeGuard;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Search;

namespace OmlTerminal.App.Views.Tools;

public sealed class SearchHitRow
{
    public required SearchHit Hit { get; init; }

    public string KindLabel => Hit.Kind switch
    {
        SearchSourceKind.Session => "SESSION",
        SearchSourceKind.Backup => "BACKUP",
        SearchSourceKind.Snapshot => "CAPTURE",
        _ => "LOG",
    };

    public string Title => Hit.LineNumber > 0 ? $"{Hit.Source}  ·  line {Hit.LineNumber}" : Hit.Source;
    public Brush MatchBrush => ToolUi.Brush(Hit.Match is "exact" or "MAC" or "text" ? "OmlMintBrush" : "OmlAmberBrush");
    public Visibility ContextVisibility => string.IsNullOrEmpty(Hit.Context) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class GlobalSearchView : UserControl, IToolView
{
    private const int MaxResults = 5000;
    private readonly ToolContext _ctx;
    private CancellationTokenSource? _cts;
    private string? _previewPath;
    private string[] _previewLines = [];

    public GlobalSearchView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        Loaded += (_, _) => FocusSearch();
    }

    public void FocusSearch() => DispatcherQueue.TryEnqueue(() =>
    {
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    });

    private void QueryBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Search_Click(sender, e); }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var t = QueryBox.Text.Trim();
        if (t.Length == 0) { QueryKindText.Text = ""; return; }
        var q = SearchQuery.Parse(t);
        QueryKindText.Text = q.Kind switch
        {
            QueryKind.Ipv4 => "IP address · plus subnets/ranges containing it",
            QueryKind.Network => $"Network {q.Network!.Cidr} · inside or overlapping",
            QueryKind.Mac => "MAC address · any notation",
            _ => "Text · case-insensitive",
        };
    }

    private sealed record Sources(bool Backups, bool AllVersions, bool Snapshots, bool Logs, IReadOnlyList<string> BackupRoots, string LogDirectory);

    /// <summary>Checkbox state and folders, read on the UI thread; the directory walk itself happens on the worker.</summary>
    private Sources CurrentSources()
    {
        var s = _ctx.Settings;
        var roots = new List<string> { s.BackupDirectory, ConfigBackup.DefaultDirectory, Path.Combine(Core.Persistence.AppPaths.DataDirectory, "migration-pulls") };
        roots.AddRange(s.ScheduledBackups.Select(j => j.RootDirectory).Where(r => r.Length > 0));
        return new Sources(BackupsCheck.IsChecked == true, AllVersionsCheck.IsChecked == true, SnapshotsCheck.IsChecked == true,
            LogsCheck.IsChecked == true, roots, s.LogDirectory);
    }

    private static List<SearchFile> FilesToSearch(Sources src)
    {
        var files = new List<SearchFile>();
        if (src.Backups) files.AddRange(GlobalSearch.BackupFiles(src.BackupRoots, src.AllVersions));
        if (src.Snapshots) files.AddRange(GlobalSearch.SnapshotFiles(GuardStore.DefaultRoot));
        if (src.Logs) files.AddRange(GlobalSearch.LogFiles(src.LogDirectory));
        return files;
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        var text = QueryBox.Text.Trim();
        if (text.Length < 2) { StatusText.Text = "TYPE AT LEAST TWO CHARACTERS"; return; }
        var query = SearchQuery.Parse(text);

        var hits = new List<SearchHit>();
        if (SessionsCheck.IsChecked == true) hits.AddRange(GlobalSearch.SearchSessions(_ctx.Model.Sessions.ToList(), query));

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SearchButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ResultList.ItemsSource = null;
        PreviewBox.Text = "";
        StatusText.Text = "SEARCHING…";
        var sw = Stopwatch.StartNew();
        var sources = CurrentSources();
        int fileCount = 0;
        bool cancelled = false;
        try
        {
            var found = await Task.Run(() =>
            {
                var files = FilesToSearch(sources);
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
            }, ct);
            hits.AddRange(found);
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SearchButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }

        var ordered = hits
            .OrderBy(h => GlobalSearch.Relevance(h.Match))
            .ThenBy(h => h.Kind switch { SearchSourceKind.Session => 0, SearchSourceKind.Backup => 1, SearchSourceKind.Snapshot => 2, _ => 3 })
            .Take(MaxResults)
            .Select(h => new SearchHitRow { Hit = h })
            .ToList();
        ResultList.ItemsSource = ordered;
        StatusText.Text = $"{ordered.Count:N0}{(ordered.Count >= MaxResults ? "+" : "")} RESULT{(ordered.Count == 1 ? "" : "S")} · {fileCount:N0} FILES · {sw.Elapsed.TotalSeconds:0.0}s{(cancelled ? " · STOPPED" : "")}";
        if (ordered.Count > 0) ResultList.SelectedIndex = 0;
        else PreviewBox.Text = fileCount == 0 && hits.Count == 0
            ? "Nothing to search yet - take some config backups, turn on session logging, or run Change Guard."
            : "No matches.";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = ResultList.SelectedItem as SearchHitRow;
        OpenSessionButton.IsEnabled = row is not null && SessionFor(row.Hit) is not null;
        ShowFileButton.IsEnabled = row?.Hit.FilePath is not null;
        if (row is null) { PreviewTitle.Text = "PREVIEW"; PreviewBox.Text = ""; return; }

        var hit = row.Hit;
        if (hit.FilePath is null)
        {
            PreviewTitle.Text = "SESSION";
            var s = _ctx.Model.Sessions.FirstOrDefault(x => x.Display == hit.Source);
            PreviewBox.Text = s is null ? hit.Line : string.Join("\n", new[]
            {
                $"Name      {s.Name}", $"Folder    {s.Folder}", $"Protocol  {s.ProtocolLabel}", $"Host      {s.Host}:{s.Port}",
                $"Username  {s.Username}", $"Tags      {s.Tags}", $"Notes     {s.Notes}",
            });
            return;
        }

        PreviewTitle.Text = Path.GetFileName(hit.FilePath).ToUpperInvariant();
        try
        {
            if (_previewPath != hit.FilePath) { _previewLines = GlobalSearch.ReadAllLinesShared(hit.FilePath); _previewPath = hit.FilePath; }
            int from = Math.Max(0, hit.LineNumber - 1 - 14), to = Math.Min(_previewLines.Length, hit.LineNumber - 1 + 15);
            int width = to.ToString().Length;
            var sb = new StringBuilder();
            if (hit.Context.Length > 0) sb.AppendLine($"[{hit.Context}]").AppendLine();
            for (int i = from; i < to; i++)
                sb.AppendLine($"{(i == hit.LineNumber - 1 ? "▶" : " ")} {(i + 1).ToString().PadLeft(width)}  {_previewLines[i]}");
            PreviewBox.Text = sb.ToString();
        }
        catch (Exception ex) { PreviewBox.Text = ex.Message; }
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

    private void OpenSession_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is SearchHitRow row && SessionFor(row.Hit) is { } s) _ctx.OpenSession(s);
    }

    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is SearchHitRow { Hit.FilePath: { } path }) ToolUi.OpenInExplorer(path);
    }

    private void CopyLine_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is SearchHitRow row) ToolUi.Copy(row.Hit.Line);
    }

    public void Shutdown() => _cts?.Cancel();
}
