using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.App.ViewModels;
using OmlTerminal.Core.Macros;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Shells;
using OmlTerminal.Core.Terminal;
using OmlTerminal.Core.Transports;
using OmlTerminal.Core.Workflows;
using OmlTerminal.Desktop.Controls;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

public partial class MainWindow : Window
{
    public const string GuideUrl = "https://omllabs.com/oml-terminal.html";

    private readonly MainViewModel _vm = new();
    private readonly Dictionary<TabItem, (TerminalTabViewModel Vm, TerminalView View)> _tabs = new();
    private readonly Dictionary<string, TabItem> _toolTabs = new();
    private (TerminalTabViewModel Vm, TerminalView View)? _lastTerminal;
    private ToolContext? _toolContext;
    private readonly JsonMacroStore _macroStore = new();
    private List<Macro> _macros = new();
    private MacroRecorder? _recorder;
    private TerminalSession? _recordingSession;
    private Action<byte[]>? _recordHandler;
    private readonly HashSet<TerminalTabViewModel> _multiExecExcluded = new();
    private readonly JsonWorkflowStore _workflowStore = new();

    /// <summary>The main window, for tool helpers that need a TopLevel (clipboard, file pickers).</summary>
    public static MainWindow? Current { get; private set; }
    private IReadOnlyList<TerminalMatch> _matches = [];
    private int _matchIndex = -1;
    private double _fontSize;
    // Opaque, so the badge stays readable on the accent-coloured selected row too.
    private static readonly IBrush BadgeBack = new SolidColorBrush(Color.FromRgb(0x1B, 0x2B, 0x36));
    private static readonly IBrush BadgeFore = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC));

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        _fontSize = _vm.Settings.FontSize is >= MainViewModel.MinFont and <= MainViewModel.MaxFont ? _vm.Settings.FontSize : MainViewModel.DefaultFont;
        Title = $"OML Terminal {AppVersionText}";
        SearchBox.TextChanged += (_, _) => RefreshTree();
        Tabs.SelectionChanged += (_, _) => OnTabChanged();
        FindBox.KeyDown += FindBox_KeyDown;
        FindBox.TextChanged += (_, _) => RunFind(keepPosition: false);
        FindCase.IsCheckedChanged += (_, _) => RunFind(keepPosition: false);
        BuildShellsMenu();
        BuildToolList();
        try { _macros = _macroStore.Load(); } catch { _macros = new(); }
        RebuildMacroMenus();
        HighlightMenu.IsChecked = _vm.Settings.HighlightKeywords;
        MultiExecBox.FontFamily = Ui.Mono;
        MultiExecBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SendMultiExec(); } };
        UpdateStartPanel();
        Opened += async (_, _) =>
        {
            if (_vm.IsLocked && !await UnlockAsync()) { Close(); return; }
            RefreshTree();
            Core.Snmp.TrafficGrapher.Shared.Start(); // MRTG polling runs while the app is open, tool tab or not
            // OML_OPEN_TOOL=pathtrace,mrtg opens tools at startup (demos, screenshots, scripted tests).
            foreach (var id in (Environment.GetEnvironmentVariable("OML_OPEN_TOOL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                OpenTool(id);
            if (_vm.Settings.RestoreWorkspaceOnLaunch)
                foreach (var id in _vm.Settings.WorkspaceSessionIds.ToList())
                    if (_vm.Sessions.FirstOrDefault(x => x.Id == id) is { } saved) Connect(saved, remember: false);
            StartBackupScheduler();
            if (!_vm.Settings.WelcomeShown && Environment.GetEnvironmentVariable("OML_OPEN_TOOL") is null) await ShowWelcomeAsync();
            await CheckForUpdatesAsync(manual: false);
        };
    }

    public static Version AppVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string AppVersionText => $"{AppVersion.Major}.{AppVersion.Minor}.{AppVersion.Build}";

    private async Task<bool> UnlockAsync()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var pw = await Dialogs.PromptAsync(this, "Unlock OML Terminal",
                attempt == 0 ? "Enter your master password to open your saved sessions." : "Wrong password - try again.",
                password: true, okText: "Unlock");
            if (pw is null) return false;
            if (_vm.TryUnlock(pw)) return true;
        }
        return false;
    }

    // ---------- session tree ----------

    /// <summary>Reloads the sidebar after a tool added or changed sessions.</summary>
    public void RefreshSessions() => RefreshTree();

    private void RefreshTree()
    {
        if (_vm.IsLocked) return;
        var (folders, unfiled) = _vm.BuildTree(SearchBox.Text);
        bool filtering = !string.IsNullOrWhiteSpace(SearchBox.Text);
        var items = new List<TreeViewItem>();
        foreach (var f in folders) items.Add(FolderItem(f, filtering));
        foreach (var s in unfiled) items.Add(SessionItem(s));
        SessionTree.ItemsSource = items;
        if (items.Count == 0)
            SessionTree.ItemsSource = new[] { new TreeViewItem { Header = new TextBlock { Text = filtering ? "No matches" : "No saved sessions yet", Classes = { "muted" } }, IsEnabled = false } };
    }

    private TreeViewItem FolderItem(SessionTreeFolder folder, bool expand)
    {
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = folder.HeaderText, FontWeight = FontWeight.SemiBold },
            IsExpanded = expand || folder.TotalCount <= 12,
        };
        var children = new List<TreeViewItem>();
        foreach (var sub in folder.Subfolders) children.Add(FolderItem(sub, expand));
        foreach (var s in folder.Sessions) children.Add(SessionItem(s));
        item.ItemsSource = children;
        return item;
    }

    private TreeViewItem SessionItem(SessionProfile p)
    {
        var badge = p.Protocol switch
        {
            ProtocolKind.Ssh => "SSH", ProtocolKind.Telnet => "TEL", ProtocolKind.Serial => "COM",
            ProtocolKind.Local => "SH", var other => other.ToString().ToUpperInvariant(),
        };
        var header = new DockPanel { LastChildFill = true };
        var tag = new Border
        {
            Background = BadgeBack, CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = badge, FontSize = 10, Foreground = BadgeFore },
        };
        DockPanel.SetDock(tag, Dock.Left);
        header.Children.Add(tag);
        header.Children.Add(new TextBlock { Text = p.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        ToolTip.SetTip(header, DescribeTarget(p));

        var item = new TreeViewItem { Header = header, Tag = p };
        item.DoubleTapped += (_, e) => { e.Handled = true; Connect(p); };
        item.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Connect(p); } };

        var connect = new MenuItem { Header = "Connect" };
        connect.Click += (_, _) => Connect(p);
        var edit = new MenuItem { Header = "Edit..." };
        edit.Click += async (_, _) => await EditSessionAsync(p);
        var duplicate = new MenuItem { Header = "Duplicate" };
        duplicate.Click += (_, _) => Duplicate(p);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += async (_, _) => await DeleteSessionAsync(p);
        var sftp = new MenuItem { Header = "Browse Files (SFTP)...", IsVisible = p.IsSshBased };
        sftp.Click += (_, _) => OpenSftp(p);
        item.ContextMenu = new ContextMenu { ItemsSource = new Control[] { connect, sftp, edit, duplicate, new Separator(), delete } };
        return item;
    }

    private static string DescribeTarget(SessionProfile p) => p.Protocol switch
    {
        ProtocolKind.Serial => $"{p.SerialPortName} @ {p.BaudRate} baud",
        ProtocolKind.Local => string.IsNullOrWhiteSpace(p.LocalShellPath) ? "Default shell" : p.LocalShellPath,
        _ when string.IsNullOrEmpty(p.Username) => $"{p.Host}:{p.Port}",
        _ => $"{p.Username}@{p.Host}:{p.Port}",
    };

    private async void NewSession_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var created = await new SessionWindow(null, _vm.AllFolders()).ShowDialog<SessionProfile?>(this);
        if (created is null) return;
        if (TrySave(() => _vm.Add(created))) { RefreshTree(); Status($"Saved \"{created.Name}\"."); }
    }

    private async Task EditSessionAsync(SessionProfile p)
    {
        var updated = await new SessionWindow(p, _vm.AllFolders()).ShowDialog<SessionProfile?>(this);
        if (updated is null) return;
        if (TrySave(() => _vm.Replace(p, updated))) { RefreshTree(); Status($"Saved \"{updated.Name}\"."); }
    }

    private void Duplicate(SessionProfile p)
    {
        var copy = p.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = p.Name + " (copy)";
        if (TrySave(() => _vm.Add(copy))) RefreshTree();
    }

    private async Task DeleteSessionAsync(SessionProfile p)
    {
        if (!await Dialogs.ConfirmAsync(this, "Delete session", $"Delete \"{p.Name}\"? This can't be undone.", "Delete")) return;
        if (TrySave(() => _vm.Remove(p))) RefreshTree();
    }

    private bool TrySave(Action save)
    {
        try { save(); return true; }
        catch (SessionValidationException ex) { _ = Dialogs.MessageAsync(this, "Can't save session", string.Join("\n", ex.Errors)); }
        catch (Exception ex) { _ = Dialogs.MessageAsync(this, "Can't save session", ex.Message); }
        return false;
    }

    // ---------- import / export ----------

    private async void ImportPutty_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var found = PuttySessionImporter.FindSessions();
        if (found.Count == 0)
        {
            await Dialogs.MessageAsync(this, "Import from PuTTY", "No PuTTY saved sessions were found" + (OperatingSystem.IsWindows() ? "." : " in ~/.putty/sessions."));
            return;
        }
        int ppk = found.Count(f => f.KeyNeedsConversion);
        await ImportAsync(found.Select(f => f.Profile).ToList(), "Import from PuTTY", "PuTTY",
            ppk > 0 ? $"{ppk} use a .ppk key, which needs converting to OpenSSH format (puttygen key.ppk -O private-openssh -o key) - they're imported with password login meanwhile." : null);
    }

    private async void ImportMobaXterm_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var path = await ToolUi.PickOpenPathAsync("Import from MobaXterm", ("MobaXterm sessions", ["*.mxtsessions", "*.ini", "*.mobaconf", "*.txt"]));
        if (path is null) return;
        List<ImportResult> found;
        try { found = MobaXtermImporter.Parse(await File.ReadAllTextAsync(path)); }
        catch (ImportException ex) { await Dialogs.MessageAsync(this, "Import from MobaXterm", ex.Message); return; }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't read the file", ex.Message); return; }
        await ImportResultsAsync(found, "MobaXterm", Path.GetFileName(path));
    }

    private async void ImportSecureCrt_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        // SecureCRT keeps each session as an .ini under Config/Sessions; ask for that folder (and any config passphrase).
        var pass = await Dialogs.PromptAsync(this, "Import from SecureCRT",
            "Next, pick your SecureCRT \"Sessions\" folder (usually ~/.vandyke/SecureCRT/Config/Sessions). If you set a Configuration Passphrase in SecureCRT, enter it here so saved passwords can be decrypted - otherwise leave it blank.",
            password: true, okText: "Choose folder...");
        if (pass is null) return;
        var folder = await ToolUi.PickFolderAsync();
        if (folder is null) return;
        List<ImportResult> found;
        try { found = SecureCrtImporter.ScanDirectory(folder, pass); }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't read SecureCRT sessions", ex.Message); return; }
        await ImportResultsAsync(found, "SecureCRT", Path.GetFileName(folder));
    }

    private async void ImportFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var path = await ToolUi.PickOpenPathAsync("Import sessions", ("Sessions (JSON or CSV)", ["*.json", "*.csv"]));
        if (path is null) return;
        List<SessionProfile> found;
        try
        {
            found = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? SessionCsv.Import(await File.ReadAllTextAsync(path))
                : new JsonSessionStore(path).Load();
        }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't read the file", ex.Message); return; }
        // A re-imported export would otherwise collide with the sessions it came from.
        foreach (var p in found) p.Id = Guid.NewGuid();
        await ImportAsync(found, "Import sessions", Path.GetFileName(path));
    }

    private async Task ImportResultsAsync(List<ImportResult> found, string source, string sourceName)
    {
        int notes = found.Count(f => f.Note is not null);
        await ImportAsync(found.Select(f => f.Profile).ToList(), $"Import from {source}", sourceName,
            notes > 0 ? $"{notes} session(s) need their password re-entered (saved passwords couldn't be brought over)." : null);
    }

    private async Task ImportAsync(List<SessionProfile> found, string title, string sourceName, string? note = null)
    {
        if (found.Count == 0) { await Dialogs.MessageAsync(this, title, $"No sessions were found in {sourceName}."); return; }
        var chosen = await new ImportWindow(found, sourceName, _vm.Sessions.ToList(), note).ShowDialog<List<SessionProfile>?>(this);
        if (chosen is null || chosen.Count == 0) return;
        int added = 0, failed = 0;
        foreach (var p in chosen)
        {
            try { _vm.Add(p); added++; }
            catch (Exception) { failed++; }
        }
        RefreshTree();
        Status($"Imported {added} session(s).");
        if (failed > 0) await Dialogs.MessageAsync(this, title, $"Imported {added} session(s); {failed} couldn't be added (missing host or invalid settings).");
    }

    private async void ExportSessions_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var include = await Dialogs.ChoiceAsync(this, "Export sessions",
            "Include passwords in the exported file? They're written as plain text so the file can be imported on another computer - handle it carefully.",
            "Without passwords", "With passwords");
        if (include is null) return;
        var path = await ToolUi.PickSavePathAsync("oml-terminal-sessions", ".json", "Sessions JSON - or name it .csv");
        if (path is null) return;
        try
        {
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) await File.WriteAllTextAsync(path, SessionCsv.Export(_vm.Sessions, include.Value));
            else new JsonSessionStore(path) { PlainTextSecrets = include.Value }.Save(_vm.Sessions.Select(s => include.Value ? s.Clone() : ShareSafeCopy(s, keepNotes: true)));
            Status($"Exported {_vm.Sessions.Count} session(s) to {path}");
        }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't export sessions", ex.Message); }
    }

    private async void ExportShareable_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var path = await ToolUi.PickSavePathAsync("oml-terminal-shareable-config", ".json", "Shareable JSON (secrets removed) - or name it .csv");
        if (path is null) return;
        try
        {
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) await File.WriteAllTextAsync(path, SessionCsv.Export(_vm.Sessions, includeSecrets: false));
            else new JsonSessionStore(path).Save(_vm.Sessions.Select(s => ShareSafeCopy(s)));
            await Dialogs.MessageAsync(this, "Shareable config exported",
                $"Saved {_vm.Sessions.Count} profiles without passwords, credential links, local key paths, startup commands or notes. Host names and usernames remain in the file - share it only with the intended team.\n{path}");
        }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't export config", ex.Message); }
    }

    private static SessionProfile ShareSafeCopy(SessionProfile profile, bool keepNotes = false)
    {
        var copy = profile.Clone();
        copy.Password = copy.EnablePassword = copy.PrivateKeyPassphrase = copy.JumpPassword = "";
        copy.PrivateKeyPath = "";
        copy.CredentialId = null;
        if (!keepNotes) { copy.StartupCommands = ""; copy.Notes = ""; }
        return copy;
    }

    // ---------- tabs ----------

    private void LocalShell_Click(object? sender, RoutedEventArgs e) =>
        Connect(new SessionProfile { Name = "Local Shell", Protocol = ProtocolKind.Local }, remember: false);

    private void BuildShellsMenu()
    {
        var items = new List<MenuItem>();
        foreach (var shell in ShellCatalog.Detect())
        {
            var mi = new MenuItem { Header = shell.Name };
            mi.Click += (_, _) => Connect(new SessionProfile
            {
                Name = shell.Name.Replace(" (login shell)", ""), Protocol = ProtocolKind.Local,
                LocalShellPath = shell.Path, LocalShellArgs = shell.Arguments,
            }, remember: false);
            items.Add(mi);
        }
        ShellsMenu.ItemsSource = items;
        ShellsMenu.IsEnabled = items.Count > 0;
    }

    /// <summary>One terminal (session + view). A tab holds one pane, or two when split.</summary>
    private sealed record Pane(TerminalTabViewModel Vm, TerminalView View, Avalonia.Controls.Shapes.Ellipse Dot);

    /// <summary>A split tab: both panes, the grid hosting them, and whether typing is mirrored.</summary>
    private sealed class SplitState
    {
        public required Pane A { get; set; }
        public required Pane B { get; set; }
        public required Grid Host { get; init; }
        public bool Sync { get; set; }
        public Pane Other(Pane p) => p == A ? B : A;
    }

    private readonly Dictionary<TabItem, SplitState> _splits = new();
    private readonly Dictionary<TabItem, TextBlock> _tabTitles = new();
    private readonly Dictionary<TerminalView, Pane> _panes = new();

    /// <summary>Builds a connected-on-layout terminal for a saved (or ad-hoc) profile, or null after telling the user why not.</summary>
    private Pane? CreatePane(SessionProfile saved)
    {
        var p = _vm.Resolve(saved);
        if (p.Protocol is not (ProtocolKind.Ssh or ProtocolKind.Telnet or ProtocolKind.Serial or ProtocolKind.Local))
        {
            _ = Dialogs.MessageAsync(this, "Not available yet", $"{p.Protocol} sessions aren't available in the Linux/macOS app yet - they're coming in a later update.");
            return null;
        }
        ITerminalTransport transport;
        try { transport = TransportFactory.Create(p); }
        catch (Exception ex) { _ = Dialogs.MessageAsync(this, "Can't open session", ex.Message); return null; }

        var engine = new XTermEngine(scrollback: Math.Clamp(_vm.Settings.ScrollbackLines, 500, 200_000));
        if (_vm.Settings.HighlightKeywords) engine.Highlights = HighlightRuleSet.Default;
        var session = new TerminalSession(transport, engine);
        if (_vm.Settings.AutoReconnect && p.Protocol is ProtocolKind.Ssh or ProtocolKind.Telnet)
            session.ReconnectTransportFactory = () => TransportFactory.Create(_vm.Resolve(saved));
        var tabVm = new TerminalTabViewModel(p, session);
        if (_vm.Settings.AlwaysLogSessions)
        {
            try { tabVm.Logger = SessionLogger.Start(session, _vm.Settings.LogDirectory, p.Name); } catch { }
        }

        var view = new TerminalView { TerminalFontSize = _fontSize };
        view.TerminalFontFamily = _vm.Settings.FontFamily;
        view.Session = session;
        view.Ready += (cols, rows) => _ = tabVm.ConnectAsync(cols, rows);
        view.GridSizeChanged += (cols, rows) => { if (CurrentTab()?.View == view) StatusSize.Text = $"{cols} × {rows}"; };
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = (IBrush)this.FindResource("OmlAccent")!, VerticalAlignment = VerticalAlignment.Center };
        var pane = new Pane(tabVm, view, dot);
        _panes[view] = pane;
        view.GotFocus += (_, _) => OnPaneFocused(pane);
        view.UserInput += bytes =>
        {
            // Synchronized typing: keystrokes in one pane go to the other pane's session too.
            if (TabOf(pane) is { } t && _splits.TryGetValue(t, out var split) && split.Sync && split.Other(pane).Vm.Session.IsConnected)
                split.Other(pane).Vm.Session.Send(bytes);
        };

        session.Ended += _ => Dispatcher.UIThread.Post(() =>
        {
            dot.Fill = Brushes.Gray;
            if (CurrentTab()?.Vm == tabVm) Status($"{p.Name}: disconnected");
        });
        session.Reconnected += () => Dispatcher.UIThread.Post(() => dot.Fill = (IBrush)this.FindResource("OmlAccent")!);
        return pane;
    }

    private void Connect(SessionProfile saved, bool remember = true)
    {
        if (CreatePane(saved) is not { } pane) return;
        var p = pane.Vm.Profile;
        var title = new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "×", FontSize = 10, Padding = new Thickness(5, 1), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, "Close tab (Ctrl+W)");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { pane.Dot, title, close } };
        ToolTip.SetTip(header, $"{pane.Vm.ProtocolText} · {pane.Vm.HostText}");
        var tab = new TabItem { Header = header, Content = pane.View, FontSize = 13 };
        close.Click += (_, _) => CloseTab(tab);
        header.PointerPressed += (_, e) => { if (e.GetCurrentPoint(header).Properties.IsMiddleButtonPressed) CloseTab(tab); };

        _tabs[tab] = (pane.Vm, pane.View);
        _tabTitles[tab] = title;
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateStartPanel();
        if (remember && !_vm.IsLocked && _vm.Sessions.Contains(saved)) { _vm.NoteRecent(saved); }
        Dispatcher.UIThread.Post(() => pane.View.Focus(), DispatcherPriority.Background);
    }

    private TabItem? TabOf(Pane pane) =>
        _tabs.FirstOrDefault(kv => kv.Value.View == pane.View || (_splits.TryGetValue(kv.Key, out var s) && (s.A == pane || s.B == pane))).Key;

    /// <summary>Every open terminal, both panes of split tabs included.</summary>
    private IEnumerable<(TerminalTabViewModel Vm, TerminalView View)> AllPanes() =>
        _tabs.Keys.SelectMany(t => _splits.TryGetValue(t, out var s)
            ? new[] { (s.A.Vm, s.A.View), (s.B.Vm, s.B.View) }
            : new[] { _tabs[t] });

    /// <summary>In a split tab, the focused pane becomes the tab's "current" terminal for every menu action.</summary>
    private void OnPaneFocused(Pane pane)
    {
        if (TabOf(pane) is not { } tab || !_splits.TryGetValue(tab, out var split)) return;
        if (_tabs[tab].View == pane.View) return;
        _tabs[tab] = (pane.Vm, pane.View);
        PaintSplit(split, pane);
        if (Tabs.SelectedItem == tab) OnTabChanged();
    }

    // ---------- split view ----------

    private void SplitRight_Click(object? sender, RoutedEventArgs e) => _ = SplitAsync(horizontal: true);
    private void SplitDown_Click(object? sender, RoutedEventArgs e) => _ = SplitAsync(horizontal: false);

    private async Task SplitAsync(bool horizontal)
    {
        if (Tabs.SelectedItem is not TabItem tab || !_tabs.TryGetValue(tab, out var cur))
        {
            await Dialogs.MessageAsync(this, "Split view", "Select a terminal tab first, then split it to open a second session beside it.");
            return;
        }
        if (_splits.ContainsKey(tab)) { await Dialogs.MessageAsync(this, "Split view", "This tab is already split - close one pane (Session > Close Pane) to split it another way."); return; }
        var chosen = await new SessionPickerWindow(_vm.IsLocked ? [] : _vm.Sessions.ToList(), cur.Vm.Profile).ShowDialog<SessionProfile?>(this);
        if (chosen is null || CreatePane(chosen) is not { } second) return;
        if (!_tabs.ContainsKey(tab)) { CloseView(second.Vm, second.View); return; } // the tab closed while picking

        var first = _panes[cur.View];
        tab.Content = null;
        var host = new Grid();
        if (horizontal) host.ColumnDefinitions = ColumnDefinitions.Parse("*,4,*");
        else host.RowDefinitions = RowDefinitions.Parse("*,4,*");
        var splitter = new GridSplitter { Background = (IBrush)this.FindResource("OmlBorder")!, ResizeDirection = horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows };
        var frameA = PaneFrame(tab, first);
        var frameB = PaneFrame(tab, second);
        if (horizontal) { Grid.SetColumn(splitter, 1); Grid.SetColumn(frameB, 2); }
        else { Grid.SetRow(splitter, 1); Grid.SetRow(frameB, 2); }
        host.Children.Add(frameA);
        host.Children.Add(splitter);
        host.Children.Add(frameB);
        tab.Content = host;
        var split = new SplitState { A = first, B = second, Host = host };
        _splits[tab] = split;
        _tabs[tab] = (second.Vm, second.View);
        UpdateSplitHeader(tab);
        PaintSplit(split, second);
        OnTabChanged();
        Dispatcher.UIThread.Post(() => second.View.Focus(), DispatcherPriority.Background);
    }

    /// <summary>A pane with a slim caption (name, close) and a border that shows which pane has focus.</summary>
    private Border PaneFrame(TabItem tab, Pane pane)
    {
        var close = new Button { Content = "×", FontSize = 10, Padding = new Thickness(6, 0), Background = Brushes.Transparent };
        ToolTip.SetTip(close, "Close this pane");
        close.Click += (_, _) => ClosePane(tab, pane);
        var caption = new DockPanel { Background = (IBrush)this.FindResource("OmlSidebar")!, Children = {
            new TextBlock { Text = pane.Vm.Profile.Name, FontSize = 11.5, Margin = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } } };
        DockPanel.SetDock(close, Dock.Right);
        caption.Children.Insert(0, close);
        var dock = new DockPanel();
        DockPanel.SetDock(caption, Dock.Top);
        dock.Children.Add(caption);
        dock.Children.Add(pane.View);
        return new Border { BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Child = dock, Tag = pane };
    }

    private void PaintSplit(SplitState split, Pane active)
    {
        var accent = (IBrush)this.FindResource("OmlAccent")!;
        foreach (var frame in split.Host.Children.OfType<Border>())
            frame.BorderBrush = frame.Tag == active ? (split.Sync ? Ui.Amber : accent) : (split.Sync ? Ui.Amber : Brushes.Transparent);
    }

    private void UpdateSplitHeader(TabItem tab)
    {
        if (!_tabTitles.TryGetValue(tab, out var title)) return;
        title.Text = _splits.TryGetValue(tab, out var s)
            ? $"{s.A.Vm.Profile.Name} | {s.B.Vm.Profile.Name}{(s.Sync ? "  ⇄" : "")}"
            : _tabs[tab].Vm.Profile.Name;
    }

    private void ClosePane_Click(object? sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabItem tab && _tabs.TryGetValue(tab, out var cur))
        {
            if (_splits.ContainsKey(tab)) ClosePane(tab, _panes[cur.View]);
            else CloseTab(tab);
        }
    }

    /// <summary>Closes one pane of a split tab; the other pane becomes the whole tab again.</summary>
    private void ClosePane(TabItem tab, Pane pane)
    {
        if (!_splits.Remove(tab, out var split)) { CloseTab(tab); return; }
        var keep = split.Other(pane);
        foreach (var frame in split.Host.Children.OfType<Border>()) ((DockPanel)frame.Child!).Children.Clear(); // free the views from their frames
        CloseView(pane.Vm, pane.View);
        tab.Content = keep.View;
        _tabs[tab] = (keep.Vm, keep.View);
        UpdateSplitHeader(tab);
        OnTabChanged();
        Dispatcher.UIThread.Post(() => keep.View.Focus(), DispatcherPriority.Background);
    }

    private void Sync_Click(object? sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is not TabItem tab || !_splits.TryGetValue(tab, out var split))
        {
            SyncMenu.IsChecked = false;
            _ = Dialogs.MessageAsync(this, "Synchronize typing", "Split a terminal tab first (Session > Split Right or Split Down) - then what you type in one pane goes to both.");
            return;
        }
        split.Sync = SyncMenu.IsChecked;
        UpdateSplitHeader(tab);
        PaintSplit(split, _panes[_tabs[tab].View]);
        Status(split.Sync ? "Typing goes to both panes." : "Synchronized typing off.");
    }

    private void CloseView(TerminalTabViewModel vm, TerminalView view)
    {
        _panes.Remove(view);
        if (_lastTerminal?.View == view) _lastTerminal = null;
        view.Detach();
        try { vm.Close(); } catch { }
    }

    private (TerminalTabViewModel Vm, TerminalView View)? CurrentTab() =>
        Tabs.SelectedItem is TabItem t && _tabs.TryGetValue(t, out var v) ? v : null;

    private void OnTabChanged()
    {
        if (Tabs.SelectedItem is TabItem t && t.Tag is ToolDescriptor tool)
        {
            Status(tool.Title);
            StatusSize.Text = "";
        }
        else if (CurrentTab() is { } cur)
        {
            _lastTerminal = cur;
            Status($"{cur.Vm.ProtocolText} · {cur.Vm.HostText}");
            StatusSize.Text = cur.View.Cols > 0 ? $"{cur.View.Cols} × {cur.View.Rows}" : "";
            Dispatcher.UIThread.Post(() => cur.View.Focus(), DispatcherPriority.Background);
        }
        else { Status("Ready"); StatusSize.Text = ""; }
        if (FindBar.IsVisible) RunFind(keepPosition: false);
        UpdateLogMenu();
        SyncMenu.IsChecked = Tabs.SelectedItem is TabItem st && _splits.TryGetValue(st, out var sp) && sp.Sync;
    }

    private void CloseTab(TabItem tab)
    {
        if (tab.Tag is ToolDescriptor tool)
        {
            _toolTabs.Remove(tool.Id);
            try { (tab.Content as IToolView)?.Shutdown(); } catch { }
            Tabs.Items.Remove(tab);
            UpdateStartPanel();
            return;
        }
        if (!_tabs.Remove(tab, out var t)) return;
        _tabTitles.Remove(tab);
        if (_splits.Remove(tab, out var split))
        {
            CloseView(split.A.Vm, split.A.View);
            CloseView(split.B.Vm, split.B.View);
        }
        else CloseView(t.Vm, t.View);
        Tabs.Items.Remove(tab);
        UpdateStartPanel();
    }

    // ---------- sftp, tunnels, scrollback ----------

    private async void Sftp_Click(object? sender, RoutedEventArgs e)
    {
        var profile = CurrentTab()?.Vm.Profile ?? (SessionTree.SelectedItem as TreeViewItem)?.Tag as SessionProfile;
        if (profile is not { IsSshBased: true })
        {
            await Dialogs.MessageAsync(this, "SFTP Browser", "Select an SSH session tab first, or right-click a saved SSH session in the sidebar.");
            return;
        }
        OpenSftp(profile);
    }

    private void OpenSftp(SessionProfile profile)
    {
        try { new SftpWindow(_vm.Resolve(profile), _vm.Settings, _vm.SaveSettings).Show(this); }
        catch (Exception ex) { _ = Dialogs.MessageAsync(this, "SFTP Browser", ex.Message); }
    }

    private async void Tunnels_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentTab() is not { } tab || tab.Vm.Session.Transport is not ISshTunnelHost host || !tab.Vm.Session.IsConnected)
        {
            await Dialogs.MessageAsync(this, "SSH Tunnels", "Select a connected SSH session tab first. Tunnels aren't available for Telnet, serial or local shells.");
            return;
        }
        await new TunnelsWindow(host, tab.Vm.Profile.Name).ShowDialog(this);
    }

    private async void SaveScrollback_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentTab() is not { } tab) { await Dialogs.MessageAsync(this, "Save scrollback", "Select a terminal tab first."); return; }
        var name = string.Concat(tab.Vm.Profile.Name.Split(Path.GetInvalidFileNameChars()));
        try
        {
            if (await ToolUi.SaveTextAsync($"{name}-{DateTime.Now:yyyyMMdd-HHmmss}", tab.Vm.Session.Engine.GetBufferText(), ".txt", "Text") is { } path)
                Status($"Saved scrollback to {path}");
        }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't save scrollback", ex.Message); }
    }

    // ---------- macros ----------

    private void RebuildMacroMenus()
    {
        var play = new List<Control>();
        var delete = new List<Control>();
        foreach (var m in _macros)
        {
            var p = new MenuItem { Header = $"{m.Name}  ({m.Steps.Count} steps)" };
            p.Click += (_, _) => PlayMacro(m);
            play.Add(p);
            var d = new MenuItem { Header = m.Name };
            d.Click += async (_, _) => await DeleteMacroAsync(m);
            delete.Add(d);
        }
        if (_macros.Count == 0)
        {
            play.Add(new MenuItem { Header = "(no macros yet)", IsEnabled = false });
            delete.Add(new MenuItem { Header = "(no macros yet)", IsEnabled = false });
        }
        PlayMacroMenu.ItemsSource = play;
        DeleteMacroMenu.ItemsSource = delete;
    }

    private async void Record_Click(object? sender, RoutedEventArgs e)
    {
        if (_recorder is null)
        {
            if (CurrentTab() is not { } tab || !tab.Vm.Session.IsConnected)
            {
                await Dialogs.MessageAsync(this, "Record macro", "Open a connected session tab first, then start recording.");
                return;
            }
            _recorder = new MacroRecorder();
            _recordingSession = tab.Vm.Session;
            _recordHandler = data => _recorder?.OnInput(data);
            _recordingSession.InputSent += _recordHandler;
            RecordMenu.Header = "Stop _Recording Macro";
            Status($"Recording a macro on {tab.Vm.Profile.Name} - Session > Stop Recording Macro when done.");
            return;
        }

        if (_recordingSession is not null && _recordHandler is not null) _recordingSession.InputSent -= _recordHandler;
        var steps = _recorder.Finish();
        _recorder = null; _recordingSession = null; _recordHandler = null;
        RecordMenu.Header = "Start _Recording Macro";
        Status("Recording stopped.");
        if (steps.Count == 0) { await Dialogs.MessageAsync(this, "Record macro", "Nothing was recorded."); return; }

        var name = await Dialogs.PromptAsync(this, "Save macro",
            $"{steps.Count} step(s) recorded. Name the macro (e.g. Show interfaces). Macros are saved as plain text, so anything you typed while recording - including passwords - is stored.",
            okText: "Save");
        if (string.IsNullOrWhiteSpace(name)) return;
        var macro = new Macro { Name = name.Trim(), Steps = steps };
        _macros.Add(macro);
        try { _macroStore.Save(_macros); }
        catch (Exception ex) { _macros.Remove(macro); await Dialogs.MessageAsync(this, "Couldn't save macro", ex.Message); return; }
        RebuildMacroMenus();
        Status($"Saved macro \"{macro.Name}\".");
    }

    private async void PlayMacro(Macro macro)
    {
        if (_recorder is not null) { await Dialogs.MessageAsync(this, "Play macro", "Stop recording first."); return; }
        if (CurrentTab() is not { } tab || !tab.Vm.Session.IsConnected) { await Dialogs.MessageAsync(this, "Play macro", "Select a connected session tab first."); return; }
        var session = tab.Vm.Session;
        // Off the UI thread so the per-step delays never freeze the window.
        _ = Task.Run(async () => { try { await MacroPlayer.PlayAsync(session, macro); } catch (OperationCanceledException) { } });
    }

    private async Task DeleteMacroAsync(Macro macro)
    {
        if (!await Dialogs.ConfirmAsync(this, "Delete macro", $"Delete the macro \"{macro.Name}\"?", "Delete")) return;
        _macros.Remove(macro);
        try { _macroStore.Save(_macros); }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't update macros", ex.Message); }
        RebuildMacroMenus();
    }

    // ---------- multi-execution ----------

    private IEnumerable<TerminalTabViewModel> AllTerminalTabs() => AllPanes().Select(p => p.Vm);

    private IEnumerable<TerminalSession> MultiExecTargets() => AllTerminalTabs().Where(t => !_multiExecExcluded.Contains(t)).Select(t => t.Session);

    private void MultiExec_Click(object? sender, RoutedEventArgs e)
    {
        MultiExecBar.IsVisible = !MultiExecBar.IsVisible;
        if (MultiExecBar.IsVisible)
        {
            UpdateMultiExecTargetsLabel();
            MultiExecCount.Text = $"{MultiExecTargets().Count(s => s.IsConnected)} connected tab(s)";
            Dispatcher.UIThread.Post(() => MultiExecBox.Focus(), DispatcherPriority.Background);
        }
        else CurrentTab()?.View.Focus();
    }

    private void UpdateMultiExecTargetsLabel()
    {
        _multiExecExcluded.RemoveWhere(t => !AllTerminalTabs().Contains(t));
        var all = AllTerminalTabs().ToList();
        int included = all.Count(t => !_multiExecExcluded.Contains(t));
        MultiExecTargetsButton.Content = (all.Count == 0 || included == all.Count ? "All tabs" : $"{included} of {all.Count} tabs") + " ▾";
    }

    private void MultiExecTargets_Click(object? sender, RoutedEventArgs e)
    {
        var items = new List<Control>();
        var tabs = AllTerminalTabs().ToList();
        if (tabs.Count == 0) items.Add(new MenuItem { Header = "(no open session tabs)", IsEnabled = false });
        foreach (var tab in tabs)
        {
            var mi = new MenuItem
            {
                Header = tab.Session.IsConnected ? tab.Profile.Name : $"{tab.Profile.Name} (not connected)",
                ToggleType = MenuItemToggleType.CheckBox, IsChecked = !_multiExecExcluded.Contains(tab), StaysOpenOnClick = true,
            };
            mi.Click += (_, _) =>
            {
                if (mi.IsChecked) _multiExecExcluded.Remove(tab); else _multiExecExcluded.Add(tab);
                UpdateMultiExecTargetsLabel();
            };
            items.Add(mi);
        }
        if (tabs.Count > 0)
        {
            items.Add(new Separator());
            var all = new MenuItem { Header = "Select all" };
            all.Click += (_, _) => { _multiExecExcluded.Clear(); UpdateMultiExecTargetsLabel(); };
            items.Add(all);
        }
        new ContextMenu { ItemsSource = items }.Open(MultiExecTargetsButton);
    }

    private void MultiExecSend_Click(object? sender, RoutedEventArgs e) => SendMultiExec();

    private void SendMultiExec()
    {
        var text = MultiExecBox.Text;
        if (!string.IsNullOrEmpty(text)) _ = ConfirmAndSendMultiExecAsync(text);
    }

    /// <summary>Always shows exactly which sessions get the command before sending - one typo on many devices is expensive.</summary>
    private async Task ConfirmAndSendMultiExecAsync(string text)
    {
        var targets = MultiExecTargets().Where(s => s.IsConnected).ToList();
        if (targets.Count == 0) { MultiExecCount.Text = "No connected tabs"; return; }
        var names = AllTerminalTabs().Where(t => targets.Contains(t.Session)).GroupBy(t => t.Profile.Display)
            .Select(g => g.Count() > 1 ? $"{g.Key}  (×{g.Count()})" : g.Key).ToList();
        if (!await Dialogs.ConfirmAsync(this, "Review multi-device command",
                $"Send this to {targets.Count} connected session(s)?\n\n{text}\n\n{string.Join("\n", names.Take(25))}{(names.Count > 25 ? $"\n... and {names.Count - 25} more" : "")}",
                "Send to listed sessions")) return;
        var sent = MultiExecutor.Broadcast(targets, text);
        MultiExecCount.Text = $"Sent to {sent} session(s)";
        if (sent > 0) MultiExecBox.Text = "";
    }

    // ---------- troubleshooting workflows ----------

    private async void Workflows_Click(object? sender, RoutedEventArgs e)
    {
        List<TroubleshootingWorkflow> current;
        try { current = _workflowStore.Load(); }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Troubleshooting workflows", ex.Message); return; }
        var result = await new WorkflowWindow(current).ShowDialog<WorkflowWindow.Result?>(this);
        if (result is null) return;
        try { _workflowStore.Save(result.Workflows); }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't save workflows", ex.Message); return; }
        if (result.Run is not { } workflow) { Status($"Saved {result.Workflows.Count} workflow(s)."); return; }
        if (CurrentTab() is not { } tab || !tab.Vm.Session.IsConnected)
        {
            await Dialogs.MessageAsync(this, "Run workflow", "Select a connected terminal session first.");
            return;
        }
        if (!await Dialogs.ConfirmAsync(this, "Review workflow commands",
                $"Target: {tab.Vm.Profile.Display}\nWorkflow: {workflow.Name}\n\n{string.Join("\n", workflow.Commands)}", "Run on this session")) return;
        try { await WorkflowRunner.RunAsync(tab.Vm.Session, workflow); }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Workflow stopped", ex.Message); }
    }

    private async void ExportWorkflows_Click(object? sender, RoutedEventArgs e)
    {
        var workflows = _workflowStore.Load();
        if (workflows.Count == 0) { await Dialogs.MessageAsync(this, "Export workflows", "There are no saved workflows to export."); return; }
        var path = await ToolUi.PickSavePathAsync("oml-terminal-workflows", ".json", "Workflow JSON");
        if (path is null) return;
        try
        {
            new JsonWorkflowStore(path).Save(workflows);
            await Dialogs.MessageAsync(this, "Workflows exported", $"Exported {workflows.Count} workflow(s). Review the commands for embedded credentials or private addresses before sharing.\n{path}");
        }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't export workflows", ex.Message); }
    }

    private async void ImportWorkflows_Click(object? sender, RoutedEventArgs e)
    {
        var path = await ToolUi.PickOpenPathAsync("Import workflows", ("Workflow JSON", ["*.json"]));
        if (path is null) return;
        try
        {
            var incoming = new JsonWorkflowStore(path).Load();
            if (incoming.Count == 0) { await Dialogs.MessageAsync(this, "Import workflows", "No valid workflows were found in that file."); return; }
            var merged = _workflowStore.Load();
            foreach (var w in incoming)
            {
                int i = merged.FindIndex(x => x.Id == w.Id);
                if (i >= 0) merged[i] = w; else merged.Add(w);
            }
            _workflowStore.Save(merged);
            await Dialogs.MessageAsync(this, "Workflows imported", $"Imported or updated {incoming.Count} workflow(s). Review the commands before running them.");
        }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Couldn't import workflows", ex.Message); }
    }

    // ---------- keyword highlighting ----------

    private void Highlight_Click(object? sender, RoutedEventArgs e)
    {
        _vm.Settings.HighlightKeywords = HighlightMenu.IsChecked;
        try { _vm.SaveSettings(); } catch { }
        var rules = _vm.Settings.HighlightKeywords ? HighlightRuleSet.Default : null;
        foreach (var (vm, view) in AllPanes())
        {
            vm.Session.Engine.Highlights = rules;
            view.InvalidateVisual();
        }
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabItem t) CloseTab(t);
    }

    private void UpdateStartPanel()
    {
        StartPanel.IsVisible = Tabs.Items.Count == 0;
        Tabs.IsVisible = Tabs.Items.Count > 0;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Remember which saved sessions were open, in tab order, to reopen them next time.
        if (!_vm.IsLocked)
        {
            _vm.Settings.WorkspaceSessionIds = Tabs.Items.OfType<TabItem>()
                .Select(t => _tabs.TryGetValue(t, out var v) ? v.Vm.Profile.Id : (Guid?)null)
                .Where(id => id is { } g && _vm.Sessions.Any(x => x.Id == g)).Select(id => id!.Value).Distinct().ToList();
            try { _vm.SaveSettings(); } catch { }
        }
        foreach (var tab in Tabs.Items.OfType<TabItem>().ToList()) CloseTab(tab);
        try { Core.Snmp.TrafficGrapher.Shared.Dispose(); } catch { }
        _schedulerCts.Cancel();
        base.OnClosing(e);
    }

    // ---------- background jobs ----------

    private readonly CancellationTokenSource _schedulerCts = new();

    /// <summary>Scheduled config backups run once a minute while the app is open (same as the Windows app).</summary>
    private void StartBackupScheduler()
    {
        var scheduler = new Core.Backup.BackupScheduler(_vm.Settings, _vm.SaveSettings, Context.SshSessions);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) =>
        {
            try { await scheduler.TickAsync(_schedulerCts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.Report(ex); }
        };
        timer.Start();
        _schedulerCts.Token.Register(timer.Stop);
    }

    // ---------- tools ----------

    private void SidebarTab_Click(object? sender, RoutedEventArgs e)
    {
        bool tools = sender == ToolsTab;
        SessionsTab.IsChecked = !tools;
        ToolsTab.IsChecked = tools;
        SessionsPanel.IsVisible = !tools;
        ToolsPanel.IsVisible = tools;
    }

    private void BuildToolList()
    {
        var menu = new List<Control>();
        foreach (var category in ToolCatalog.Categories)
        {
            var tools = ToolCatalog.All.Where(t => t.Category == category).ToList();
            if (tools.Count == 0) continue;
            ToolList.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Classes = { "section" }, Margin = new Thickness(4, 10, 0, 4) });
            if (menu.Count > 0) menu.Add(new Separator());
            foreach (var tool in tools)
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent, Padding = new Thickness(8, 5),
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = tool.Title, FontSize = 13 },
                            new TextBlock { Text = tool.Subtitle, FontSize = 11, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                };
                ToolTip.SetTip(button, tool.Subtitle);
                button.Click += (_, _) => OpenTool(tool.Id);
                ToolList.Children.Add(button);
                var mi = new MenuItem { Header = tool.Title };
                mi.Click += (_, _) => OpenTool(tool.Id);
                menu.Add(mi);
            }
        }
        ToolsMenu.ItemsSource = menu;
    }

    private ToolContext Context => _toolContext ??= new ToolContext
    {
        Sessions = () => _vm.IsLocked ? [] : _vm.Sessions.Select(_vm.Resolve).ToList(),
        Model = _vm,
        SendToActiveSession = text => SendToTerminal(text + "\r"),
        InsertIntoActiveSession = SendToTerminal,
        Settings = _vm.Settings,
        SaveSettings = () => { try { _vm.SaveSettings(); } catch { } },
        OpenSession = p => Connect(p, remember: false),
        ActiveTerminalText = () => _lastTerminal?.Vm.Session.Engine.GetBufferText(),
        OpenTool = (id, configure) => OpenTool(id, configure),
    };

    private bool SendToTerminal(string text)
    {
        if (_lastTerminal is not { } t || !t.Vm.Session.IsConnected) return false;
        t.Vm.Session.Send(System.Text.Encoding.UTF8.GetBytes(text));
        return true;
    }

    /// <summary>Opens a tool in its own tab, or switches to it if it's already open.</summary>
    public void OpenTool(string id, Action<Control>? configure = null)
    {
        if (_toolTabs.TryGetValue(id, out var existing))
        {
            Tabs.SelectedItem = existing;
            if (existing.Content is Control c) configure?.Invoke(c);
            return;
        }
        if (ToolCatalog.Find(id) is not { } tool) return;
        Control view;
        try { view = tool.Create(Context); }
        catch (Exception ex) { _ = Dialogs.MessageAsync(this, tool.Title, ex.Message); return; }
        configure?.Invoke(view);

        var title = new TextBlock { Text = tool.Title, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Avalonia.Controls.Shapes.Rectangle { Width = 7, Height = 7, Fill = Ui.Violet, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "×", FontSize = 10, Padding = new Thickness(5, 1), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, title, close } };
        var tab = new TabItem { Header = header, Content = view, Tag = tool, FontSize = 13 };
        close.Click += (_, _) => CloseTab(tab);
        header.PointerPressed += (_, e) => { if (e.GetCurrentPoint(header).Properties.IsMiddleButtonPressed) CloseTab(tab); };
        _toolTabs[id] = tab;
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateStartPanel();
    }

    // ---------- settings / logging ----------

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_vm.Settings);
        if (!await dialog.ShowDialog<bool>(this)) return;
        try
        {
            if (dialog.RemoveMasterPassword) _vm.RemoveMasterPassword();
            else if (dialog.NewMasterPassword is { } pw) _vm.SetMasterPassword(pw);
            _vm.SaveSettings();
        }
        catch (Exception ex) { await Dialogs.MessageAsync(this, "Could not save settings", ex.Message); return; }
        _fontSize = _vm.Settings.FontSize;
        foreach (var (vm, view) in AllPanes())
        {
            view.TerminalFontFamily = _vm.Settings.FontFamily;
            view.TerminalFontSize = _fontSize;
            vm.Session.Engine.Highlights = _vm.Settings.HighlightKeywords ? HighlightRuleSet.Default : null;
        }
        Status("Settings saved.");
    }

    private async void Log_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentTab() is not { } tab || !tab.Vm.Session.IsConnected)
        {
            await Dialogs.MessageAsync(this, "Log to file", "Select a connected session tab first.");
            return;
        }
        if (tab.Vm.Logger is null)
        {
            try { tab.Vm.Logger = SessionLogger.Start(tab.Vm.Session, _vm.Settings.LogDirectory, tab.Vm.Profile.Name); }
            catch (Exception ex) { await Dialogs.MessageAsync(this, "Could not start logging", ex.Message); return; }
            Status($"Logging to {tab.Vm.Logger.Path}");
        }
        else
        {
            var path = tab.Vm.Logger.Path;
            tab.Vm.Logger.Dispose();
            tab.Vm.Logger = null;
            await Dialogs.MessageAsync(this, "Logging stopped", $"Transcript saved to:\n{path}");
        }
        UpdateLogMenu();
    }

    private void UpdateLogMenu() => LogMenu.Header = CurrentTab()?.Vm.Logger is null ? "_Log Session to File" : "Stop _Logging";

    // ---------- edit / view ----------

    private void Copy_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab()?.View.CopySelectionAsync();
    private void Paste_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab()?.View.PasteAsync();

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetFont(_fontSize + 1);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetFont(_fontSize - 1);
    private void ZoomReset_Click(object? sender, RoutedEventArgs e) => SetFont(MainViewModel.DefaultFont);

    private void SetFont(double size)
    {
        _fontSize = Math.Clamp(size, MainViewModel.MinFont, MainViewModel.MaxFont);
        foreach (var (_, view) in AllPanes()) view.TerminalFontSize = _fontSize;
        _vm.Settings.FontSize = _fontSize;
        try { _vm.SaveSettings(); } catch { }
        Status($"Font size {_fontSize}");
    }

    private void ToggleSidebar_Click(object? sender, RoutedEventArgs e)
    {
        bool show = !Sidebar.IsVisible;
        Sidebar.IsVisible = show;
        Body.ColumnDefinitions[0].Width = show ? new GridLength(260) : new GridLength(0);
    }

    private void Find_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentTab() is null) return;
        FindBar.IsVisible = true;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void RunFind(bool keepPosition)
    {
        var view = CurrentTab()?.View;
        if (view is null || string.IsNullOrEmpty(FindBox.Text))
        {
            _matches = [];
            _matchIndex = -1;
            FindCount.Text = "";
            view?.SetFindHighlight(null);
            return;
        }
        _matches = view.FindText(FindBox.Text, FindCase.IsChecked == true);
        _matchIndex = _matches.Count == 0 ? -1 : (keepPosition ? Math.Clamp(_matchIndex, 0, _matches.Count - 1) : _matches.Count - 1);
        ShowMatch();
    }

    private void ShowMatch()
    {
        var view = CurrentTab()?.View;
        FindCount.Text = _matches.Count == 0 ? "No matches" : $"{_matchIndex + 1} of {_matches.Count}";
        view?.SetFindHighlight(_matchIndex >= 0 ? _matches[_matchIndex] : null);
    }

    private void Step(int delta)
    {
        if (_matches.Count == 0) { RunFind(keepPosition: false); return; }
        _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
        ShowMatch();
    }

    private void FindNext_Click(object? sender, RoutedEventArgs e) => Step(+1);
    private void FindPrev_Click(object? sender, RoutedEventArgs e) => Step(-1);
    private void FindClose_Click(object? sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        FindBar.IsVisible = false;
        CurrentTab()?.View.SetFindHighlight(null);
        CurrentTab()?.View.Focus();
    }

    private void FindBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Step(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
    }

    // ---------- shortcuts ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var m = e.KeyModifiers;
        bool ctrl = m.HasFlag(KeyModifiers.Control), shift = m.HasFlag(KeyModifiers.Shift);
        if (!ctrl) return;
        Action? action = (shift, e.Key) switch
        {
            (false, Key.N) => () => NewSession_Click(null, e),
            (false, Key.W) => () => CloseTab_Click(null, e),
            (true, Key.T) => () => LocalShell_Click(null, e),
            (true, Key.F) => () => Find_Click(null, e),
            (true, Key.B) => () => ToggleSidebar_Click(null, e),
            (true, Key.S) => () => Sftp_Click(null, e),
            (true, Key.M) => () => MultiExec_Click(null, e),
            (true, Key.E) => () => SplitRight_Click(null, e),
            (true, Key.O) => () => SplitDown_Click(null, e),
            (true, Key.Q) => Close,
            (false, Key.OemComma) => () => Settings_Click(null, e),
            (_, Key.OemPlus or Key.Add) => () => SetFont(_fontSize + 1),
            (false, Key.OemMinus or Key.Subtract) => () => SetFont(_fontSize - 1),
            (false, Key.D0 or Key.NumPad0) => () => SetFont(MainViewModel.DefaultFont),
            (false, Key.Tab) => () => CycleTab(+1),
            (true, Key.Tab) => () => CycleTab(-1),
            (false, Key.PageDown) => () => CycleTab(+1),
            (false, Key.PageUp) => () => CycleTab(-1),
            _ => null,
        };
        if (action is null) return;
        e.Handled = true;
        action();
    }

    private void CycleTab(int delta)
    {
        int n = Tabs.Items.Count;
        if (n < 2) return;
        Tabs.SelectedIndex = (Tabs.SelectedIndex + delta + n) % n;
    }

    // ---------- help ----------

    private void Quit_Click(object? sender, RoutedEventArgs e) => Close();
    private void Guide_Click(object? sender, RoutedEventArgs e) => OpenUrl(GuideUrl);
    private void Downloads_Click(object? sender, RoutedEventArgs e) => OpenUrl(UpdateChecker.DownloadsUrl);

    private async void About_Click(object? sender, RoutedEventArgs e) =>
        await Dialogs.MessageAsync(this, "About OML Terminal",
            $"OML Terminal {AppVersionText} for {(OperatingSystem.IsMacOS() ? "macOS" : "Linux")} (preview)\n\n" +
            "SSH, Telnet, serial and local shells for network engineers.\nFree for the community, by OML Labs.\n\n" +
            $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
            $"Data folder: {AppPaths.DataDirectory}");

    private void WhatsNew_Click(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/singhranjit/OML-TERMINAL/blob/main/CHANGELOG.md");

    // ---------- updates ----------

    private ReleaseInfo? _available;

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    /// <summary>Automatic checks only if the user opted in, at most once a day; Help > Check for Updates always asks.</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var s = _vm.Settings;
        if (!manual && (s.CheckForUpdates != true || !UpdateChecker.Due(s.LastUpdateCheckUtc, DateTime.UtcNow))) return;
        ReleaseInfo? info = null;
        try { info = await UpdateChecker.CheckAsync(AppVersion); } catch { }
        s.LastUpdateCheckUtc = DateTime.UtcNow;
        try { _vm.SaveSettings(); } catch { }
        if (info is not null && (manual || info.Version != s.SkippedUpdateVersion))
        {
            _available = info;
            UpdateText.Text = $"OML Terminal {info.Version} is available - you have {AppVersionText}. " + (info.Notes.Length > 0 ? info.Notes : $"Released {info.Date}.");
            UpdateBar.IsVisible = true;
        }
        else if (manual)
            await Dialogs.MessageAsync(this, "Check for Updates", $"You're up to date - OML Terminal {AppVersionText} is the latest release. (If omllabs.com couldn't be reached, try again later.)");
    }

    // The feed's direct link is the Windows installer; Linux and macOS builds are listed on the downloads page.
    private void UpdateDownload_Click(object? sender, RoutedEventArgs e)
    {
        OpenUrl(OperatingSystem.IsWindows() && _available?.Url is { Length: > 0 } u ? u : UpdateChecker.DownloadsUrl);
        UpdateBar.IsVisible = false;
    }

    private void UpdateSkip_Click(object? sender, RoutedEventArgs e)
    {
        if (_available is not null) { _vm.Settings.SkippedUpdateVersion = _available.Version; try { _vm.SaveSettings(); } catch { } }
        UpdateBar.IsVisible = false;
    }

    private void UpdateClose_Click(object? sender, RoutedEventArgs e) => UpdateBar.IsVisible = false;

    // ---------- first run ----------

    private async void Welcome_Click(object? sender, RoutedEventArgs e) => await ShowWelcomeAsync();

    /// <summary>First launch: what the app is, where things are, the update opt-in, and import shortcuts when there are no sessions yet.</summary>
    private async Task ShowWelcomeAsync()
    {
        var updates = new CheckBox
        {
            IsChecked = _vm.Settings.CheckForUpdates ?? true,
            Content = new TextBlock { Text = "Tell me when a new version is out (checks omllabs.com once a day - nothing about you or your devices is sent)", TextWrapping = TextWrapping.Wrap },
        };
        var panel = new StackPanel { Spacing = 14, Width = 560 };
        panel.Children.Add(new TextBlock { Text = "The network engineer's cockpit - free for the community, no account, no telemetry.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, LineHeight = 22,
            Text = "•  New Session (Ctrl+N) for SSH, Telnet, serial and local shells; double-click a saved session to connect.\n" +
                   "•  The Tools tab in the sidebar holds the network toolkit - Visual Trace, Ping Monitor, MRTG graphs, Packet and Wi-Fi Analyzers, Topology, Change Guard, backups and more.\n" +
                   "•  Session menu: SFTP browser (Ctrl+Shift+S), SSH tunnels and scrollback snapshots.\n" +
                   "•  Saved logins live in the Password Manager, encrypted on this computer.",
        });
        panel.Children.Add(updates);
        bool noSessions = _vm.Sessions.Count == 0;
        if (noSessions) panel.Children.Add(new TextBlock { Text = "Coming from another terminal? Bring your saved sessions across:", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted });

        var w = new Window
        {
            Title = $"Welcome to OML Terminal {AppVersionText}", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        string choice = "";
        Button B(string text, string id, bool accent = false)
        {
            var b = new Button { Content = text, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (accent) { b.Classes.Add("accent"); b.IsDefault = true; }
            b.Click += (_, _) => { choice = id; w.Close(); };
            return b;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        if (noSessions) { buttons.Children.Add(B("Import from PuTTY", "putty")); buttons.Children.Add(B("Import from MobaXterm", "moba")); }
        buttons.Children.Add(B("Start", "", accent: true));
        panel.Children.Add(buttons);
        w.Content = new Border { Padding = new Thickness(22), Child = panel };
        await w.ShowDialog(this);

        _vm.Settings.CheckForUpdates = updates.IsChecked == true;
        _vm.Settings.WelcomeShown = true;
        try { _vm.SaveSettings(); } catch { }
        if (choice == "putty") ImportPutty_Click(this, new RoutedEventArgs());
        else if (choice == "moba") ImportMobaXterm_Click(this, new RoutedEventArgs());
    }

    private void OpenUrl(string url)
    {
        try { _ = Launcher.LaunchUriAsync(new Uri(url)); }
        catch { Status(url); }
    }

    private void Status(string text) => StatusText.Text = text;

    private Exception? _lastError;

    /// <summary>Shows an unexpected error in a banner (it's also in crash.log) instead of closing the app.</summary>
    public void ShowError(Exception ex)
    {
        _lastError = ex;
        ErrorText.Text = $"Something went wrong: {ex.GetType().Name}: {ex.Message} - details are in crash.log.";
        ErrorBar.IsVisible = true;
    }

    private void ErrorClose_Click(object? sender, RoutedEventArgs e) => ErrorBar.IsVisible = false;
    private void ErrorCopy_Click(object? sender, RoutedEventArgs e) { if (_lastError is not null) ToolUi.Copy(_lastError.ToString()); }
}
