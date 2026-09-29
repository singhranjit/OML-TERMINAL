using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Firewall;

namespace OmlTerminal.App.Views.Tools;

public sealed class PolicyRow(PolicyRule r, int number)
{
    private static string Join(IReadOnlyList<string> v) => v.Count == 0 ? "any" : string.Join(", ", v);

    public string Number => number.ToString();
    public string Name => r.Name;
    public string Comment => r.Enabled ? r.Comment : $"(disabled) {r.Comment}".Trim();
    public string Action => r.Action.ToString().ToUpperInvariant();
    public string Path => $"{(r.SourceInterface.Length > 0 ? r.SourceInterface : "any")} → {(r.DestinationInterface.Length > 0 ? r.DestinationInterface : "any")}";
    public string Source => "src  " + Join(r.Sources);
    public string Destination => "dst  " + Join(r.Destinations);
    public string Service => "svc  " + Join(r.Services) + (r.Applications.Count > 0 ? $"  ·  app {Join(r.Applications)}" : "");
    public double RowOpacity => r.Enabled ? 1 : 0.5;
    public Brush ActionBrush => ToolUi.Brush(r.Action == PolicyAction.Allow ? "OmlMintBrush" : "OmlRoseBrush");
    public Brush ActionBackground => new SolidColorBrush(r.Action == PolicyAction.Allow
        ? Windows.UI.Color.FromArgb(0x26, 0x34, 0xD3, 0x99)
        : Windows.UI.Color.FromArgb(0x26, 0xFB, 0x71, 0x85));
}

public sealed partial class FirewallPolicyView : UserControl, IToolView
{
    private readonly ToolContext _ctx;
    private GeneratedConfig? _result;
    private List<PolicyRule> _rules = new();
    private string? _sheetPath;
    private FileSystemWatcher? _watcher;
    private bool _loaded;
    private bool _loadingFile;

    /// <summary>The sheet being generated from. Kept here rather than read back from SheetBox: a hidden multi-line
    /// TextBox doesn't reliably return freshly-set text during its own TextChanged.</summary>
    private string _sheetText = "";

    private void SetSheet(string text)
    {
        _sheetText = text;
        _loadingFile = true;
        SheetBox.Text = text;
        _loadingFile = false;
        Regenerate();
    }

    public FirewallPolicyView(ToolContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        foreach (var v in Enum.GetValues<FirewallVendor>())
            VendorBox.Items.Add(new ComboBoxItem { Content = FirewallVendors.DisplayName(v), Tag = v });
        VendorBox.SelectedIndex = 0;
        Loaded += (_, _) => { _loaded = true; UpdateVendorOptions(); Regenerate(); };
    }

    private FirewallVendor Vendor => (VendorBox.SelectedItem as ComboBoxItem)?.Tag is FirewallVendor v ? v : FirewallVendor.FortiGate;

    private static bool IsCliVendor(FirewallVendor v) =>
        v is FirewallVendor.FortiGate or FirewallVendor.PaloAlto or FirewallVendor.CiscoAsa or FirewallVendor.JuniperSrx;

    // ---------- sheet loading

    private async void Sample_Click(object sender, RoutedEventArgs e)
    {
        var path = await ToolUi.SaveTextAsync("firewall-policy-sheet", SampleForExcel(), ".csv", "CSV (Excel)");
        if (path is null) return;
        LoadFile(path);
        StatusText.Text = "Sample saved and opened in Excel. Edit the rows, save (keep CSV format) - this page reloads automatically.";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Saved to {path}, but couldn't open it: {ex.Message}"; }
    }

    /// <summary>Excel shows "#" comment lines as data rows, which is fine: the parser skips them. Written with a BOM so
    /// Excel reads UTF-8 correctly.</summary>
    private static string SampleForExcel() => "﻿" + PolicySheet.Sample.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".csv", ".txt", ".tsv", ".xlsx" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        if (file.Path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "That's an Excel workbook - in Excel use File > Save As > \"CSV UTF-8 (Comma delimited)\" and open the .csv here.";
            return;
        }
        LoadFile(file.Path);
    }

    private void LoadExample_Click(object sender, RoutedEventArgs e)
    {
        StopWatching();
        _sheetPath = null;
        FileText.Text = "Built-in example (edit it in the Sheet tab)";
        SetSheet(PolicySheet.Sample);
    }

    private void LoadFile(string path)
    {
        StopWatching();
        _sheetPath = path;
        FileText.Text = path;
        ReadSheet();
        // Excel saves by writing a temp file and renaming, so watch the folder for anything touching this name.
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnSheetChanged;
        _watcher.Created += OnSheetChanged;
        _watcher.Renamed += OnSheetChanged;
    }

    private int _reloadVersion;

    private void OnSheetChanged(object sender, FileSystemEventArgs e)
    {
        int version = Interlocked.Increment(ref _reloadVersion);
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(400); // let Excel finish writing
            if (version != _reloadVersion) return;
            ReadSheet();
            StatusText.Text = $"Reloaded {Path.GetFileName(_sheetPath)} at {DateTime.Now:HH:mm:ss}.";
        });
    }

    private void ReadSheet()
    {
        if (_sheetPath is null) return;
        try
        {
            // Excel keeps the file open while editing; share everything so reading still works.
            using var fs = new FileStream(_sheetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
            SetSheet(reader.ReadToEnd());
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't read the sheet: {ex.Message}"; }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // ---------- options

    private void Vendor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdateVendorOptions();
        Regenerate();
    }

    private void UpdateVendorOptions()
    {
        var v = Vendor;
        FortiOptions.Visibility = v == FirewallVendor.FortiGate ? Visibility.Visible : Visibility.Collapsed;
        PanOptions.Visibility = v == FirewallVendor.PaloAlto ? Visibility.Visible : Visibility.Collapsed;
        CpOptions.Visibility = v == FirewallVendor.CheckPoint ? Visibility.Visible : Visibility.Collapsed;
        AsaOptions.Visibility = v == FirewallVendor.CiscoAsa ? Visibility.Visible : Visibility.Collapsed;
        var scope = (PanScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        PanScopeName.IsEnabled = scope is "vsys" or "dg";
        ApplyHint.Text = FirewallVendors.HowToApply(v);
        SendButton.IsEnabled = IsCliVendor(v);
    }

    private void Option_TextChanged(object sender, TextChangedEventArgs e) => Regenerate();
    private void Option_Click(object sender, RoutedEventArgs e) => Regenerate();
    private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) { UpdateVendorOptions(); Regenerate(); } }
    private void StartIdBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Regenerate();
    private void SheetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingFile) return;
        _sheetText = SheetBox.Text; // the user edited the sheet in place
        Regenerate();
    }

    private PolicyOptions Options()
    {
        var scopeTag = (PanScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var scopeName = PanScopeName.Text.Trim();
        return new PolicyOptions
        {
            ObjectPrefix = PrefixBox.Text.Trim(),
            IncludeRollback = RollbackBox.IsChecked == true,
            Vdom = VdomBox.Text.Trim(),
            FortiStartId = double.IsNaN(StartIdBox.Value) ? 0 : (int)Math.Min(StartIdBox.Value, int.MaxValue),
            PanScope = scopeTag switch
            {
                "shared" => "shared",
                "vsys" when scopeName.Length > 0 => $"vsys:{scopeName}",
                "dg" when scopeName.Length > 0 => $"dg:{scopeName}",
                _ => "",
            },
            CheckPointLayer = LayerBox.Text.Trim(),
            CheckPointPosition = (PositionBox.SelectedItem as ComboBoxItem)?.Content as string ?? "bottom",
            AsaAclSuffix = AclSuffixBox.Text.Trim(),
            AsaAccessGroup = AccessGroupBox.IsChecked == true,
        };
    }

    // ---------- generation

    private void Regenerate()
    {
        if (!_loaded) return;
        var (rules, issues) = PolicySheet.Parse(_sheetText);
        _rules = rules;
        RuleList.ItemsSource = rules.Select((r, i) => new PolicyRow(r, i + 1)).ToList();
        EmptyText.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewTab.Text = rules.Count == 0 ? "Rules" : $"Rules ({rules.Count})";
        IssuesBar.IsOpen = issues.Count > 0;
        IssuesBar.Message = string.Join("\n", issues.Take(10).Select(i => i.ToString())) + (issues.Count > 10 ? $"\n… and {issues.Count - 10} more" : "");

        _result = rules.Count == 0 ? null : FirewallGenerators.Policies(Vendor, rules, Options());
        WarningsBar.IsOpen = _result?.Warnings.Count > 0;
        WarningsBar.Message = _result is null ? "" : string.Join("\n", _result.Warnings);
        RollbackTab.IsEnabled = RollbackBox.IsChecked == true;
        if (RollbackBox.IsChecked != true && OutputBar.SelectedItem == RollbackTab) OutputBar.SelectedItem = OutputBar.Items[0];
        ShowOutput();
    }

    private string CurrentText => OutputBar.SelectedItem == RollbackTab ? _result?.Rollback ?? "" : _result?.Script ?? "";

    private void ShowOutput()
    {
        if (OutputBox is null) return;
        OutputBox.Text = CurrentText;
    }

    private void OutputBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowOutput();

    private void SheetBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool csv = sender.SelectedItem == CsvTab;
        SheetBox.Visibility = csv ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Visibility = csv ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        ToolUi.Copy(CurrentText);
        StatusText.Text = "Copied to clipboard.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentText.Length == 0) return;
        var (ext, type) = FirewallVendors.FileType(Vendor);
        var which = OutputBar.SelectedItem == RollbackTab ? "-rollback" : "";
        var path = await ToolUi.SaveTextAsync($"{Vendor.ToString().ToLowerInvariant()}-policies{which}-{DateTime.Now:yyyyMMdd-HHmm}", CurrentText, ext, type);
        if (path is not null) StatusText.Text = $"Saved to {path}";
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = CurrentText;
        if (text.Trim().Length == 0) { StatusText.Text = "Nothing to send yet."; return; }
        // Section headers are comments for the reader, not commands for the device.
        var lines = Core.TextLines.Split(text).Where(l => !l.StartsWith("# ----") && !l.StartsWith("! ----") && !l.StartsWith("# commit"));
        StatusText.Text = _ctx.SendToActiveSession(string.Join("\n", lines))
            ? $"Sent {_rules.Count} rule(s) to the last active terminal session. Check its output, then commit/save on the device."
            : "No connected terminal session - open an SSH tab to the firewall first.";
    }

    public void Shutdown() => StopWatching();
}
