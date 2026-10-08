using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OmlTerminal.App.Controls;
using OmlTerminal.App.ViewModels;
using OmlTerminal.App.Views;
using OmlTerminal.App.Views.Tools;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Macros;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Shells;
using OmlTerminal.Core.Terminal;
using OmlTerminal.Core.Transports;
using OmlTerminal.Core.Workflows;

namespace OmlTerminal.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly TaskCompletionSource _ready = new();
    private readonly JsonMacroStore _macroStore = new();
    private readonly JsonWorkflowStore _workflowStore = new();
    private List<Macro> _macros = new();
    private MacroRecorder? _recorder;
    private TerminalSession? _recordingSession;
    private Action<byte[]>? _recordHandler;
    private bool _dialogOpen;
    private bool _shuttingDown;

    // RDP/VNC/OML-VNC tabs used to always show a green "connected" status dot regardless of real link state -
    // these two sets give them the same three-way connected/connecting/error distinction terminal tabs already
    // had via TerminalSession.IsConnected, tracked from the same Connected/Closed events those controls already raise.
    private readonly HashSet<TabViewItem> _connectedTabs = new();
    private readonly HashSet<TabViewItem> _failedTabs = new();

    /// <summary>Folder names collapsed in the sidebar tree. In-memory only (resets on restart), like most
    /// tree-view expand state; not worth persisting alongside the session data.</summary>
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SolidColorBrush RowIdleBrush = new(Microsoft.UI.Colors.Transparent);
    private static readonly SolidColorBrush RowHoverBrush = new(Windows.UI.Color.FromArgb(18, 255, 255, 255));
    private static readonly SolidColorBrush RowDragOverBrush = new(Windows.UI.Color.FromArgb(50, 56, 189, 248));

    // ---------- manual sidebar-tree drag state (see Row_PointerPressed) ----------
    private Panel? _dragSourceRow;
    private object? _dragSourceTag;
    private Windows.Foundation.Point _dragStartPoint;
    private bool _dragActive;
    private Panel? _dragHoverTarget;

    /// <summary>Every currently-built folder row, rebuilt alongside the tree each RefreshSessionList - used to
    /// hit-test drop targets manually (VisualTreeHelper.FindElementsInHostCoordinatesPoint isn't available here).</summary>
    private readonly List<Panel> _folderRows = new();

    /// <summary>Where each open terminal reports its title and connection state - its tab header while it has a tab
    /// to itself, or its pane header once it has been moved into a split. Keyed per terminal so a session keeps
    /// reporting to the right place however its tab is rearranged.</summary>
    private readonly Dictionary<TerminalTabViewModel, (Action<string> Title, Action<bool, bool> State)> _sinks = new();

    /// <summary>The terminal the user last worked in, so a tool tab (e.g. the firewall builder) can "send to session"
    /// even while the tool itself is the selected tab.</summary>
    private TerminalTabViewModel? _lastTerminal;

    private readonly XServerManager _xserver = new();
    private readonly DispatcherTimer _xserverPoll = new() { Interval = TimeSpan.FromSeconds(8) };

    /// <summary>Runs OmlTerminal.Core.Backup.ScheduledBackupJob entries while the app is open - a once-a-minute
    /// check, independent of whether the Scheduled Backups tool tab is open.</summary>
    private BackupScheduler? _backupScheduler;
    private readonly DispatcherTimer _backupSchedulerTick = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly CancellationTokenSource _backupSchedulerCts = new();
    private ToolContext _toolContext = null!;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 860));
        // The exe's embedded icon (ApplicationIcon in the csproj) covers File Explorer/shortcuts, but an
        // unpackaged WinUI3 app's own taskbar/Alt-Tab/title-bar icon needs this set explicitly at runtime too.
        try { AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 226, 232, 240);
        tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 56, 189, 248);
        tb.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        Home.OmlNodeRequested += async () => await OmlNodeAsync();

        _vm.Sessions.CollectionChanged += (_, _) => { RefreshSessionList(); Home.SetRecent(_vm.Recent()); };
        Home.QuickConnectRequested += async text => await QuickConnectAsync(text);
        Home.SessionChosen += p => OpenSession(p);
        Closed += (_, _) => { _shuttingDown = true; foreach (var t in Tabs.TabItems.OfType<TabViewItem>().ToList()) CloseTabItem(t); };

        // OEM plus/minus and 0 have no named VirtualKey members.
        ZoomInItem.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = (Windows.System.VirtualKey)187 });
        ZoomOutItem.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = (Windows.System.VirtualKey)189 });
        ZoomResetItem.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Number0 });

        RefreshSessionList();
        Home.SetRecent(_vm.Recent());
        UpdateStatus();

        foreach (var bar in MainMenu.Items) HookMenuRepaint(bar.Items);
        _macros = _macroStore.Load();
        RebuildMacroMenus();
        HighlightItem.IsChecked = _vm.Settings.HighlightKeywords;

        _toolContext = new ToolContext
        {
            Sessions = () => _vm.Sessions.Select(_vm.Resolve).ToList(),
            Model = _vm,
            SendToActiveSession = SendToActiveSession,
            InsertIntoActiveSession = InsertIntoActiveSession,
            Settings = _vm.Settings,
            SaveSettings = _vm.SaveSettings,
            OpenSession = OpenSession,
            ActiveTerminalText = () => (SelectedTerminalTab() ?? _lastTerminal)?.Session.Engine.GetBufferText(),
        };
        ToolsList.ItemsSource = ToolCatalog.All;
        BuildToolsMenu();
        BuildToolbar();
        Home.ToolRequested += id => OpenTool(id);
        Home.NewSessionRequested += async kind => await NewSessionAsync(new SessionProfile { Protocol = kind, Port = SessionProfile.DefaultPortFor(kind) });

        _xserver.Display = _vm.Settings.XDisplay;
        _xserver.ConfiguredPath = _vm.Settings.XServerPath;
        _xserver.StateChanged += () => DispatcherQueue.TryEnqueue(() => _ = RefreshXServerIndicatorAsync());
        _xserverPoll.Tick += async (_, _) => await RefreshXServerIndicatorAsync();
        _xserverPoll.Start();

        _backupScheduler = new BackupScheduler(_vm.Settings, _vm.SaveSettings, _toolContext.SshSessions);
        _backupSchedulerTick.Tick += async (_, _) => await _backupScheduler.TickAsync(_backupSchedulerCts.Token);
        _backupSchedulerTick.Start();
        Closed += (_, _) => { _xserverPoll.Stop(); _xserver.Stop(); _backupSchedulerTick.Stop(); _backupSchedulerCts.Cancel(); };

        RootGrid.Loaded += async (_, _) =>
        {
            await UnlockIfNeededAsync();
            if (_vm.Settings.StartXServerOnLaunch) await StartXServerOnLaunchAsync();
            else await RefreshXServerIndicatorAsync();
            if (_vm.Settings.RestoreWorkspaceOnLaunch)
                foreach (var id in _vm.Settings.WorkspaceSessionIds.ToList())
                    if (_vm.Sessions.FirstOrDefault(s => s.Id == id) is { } profile) OpenSession(profile);
        };
    }

    // ---------- repaint after popups ----------

    // The terminal canvas is left blank when a menu or dialog closes over it, until something invalidates it again.
    private void HookMenuRepaint(IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            if (item is MenuFlyoutSubItem sub) HookMenuRepaint(sub.Items);
            // After a menu command, typing should land in the terminal again - not on whatever control the menu
            // hands focus back to (in a split that can be a pane's close button, where Enter would close it).
            else if (item is MenuFlyoutItem mi) mi.Click += (_, _) =>
            {
                RepaintSoon();
                DispatcherQueue.TryEnqueue(() => { if (!_dialogOpen) FocusActiveTerminal(); });
            };
        }
    }

    private async void RepaintSoon()
    {
        foreach (var ms in new[] { 80, 300, 700 })
        {
            await Task.Delay(ms);
            if (Tabs.SelectedItem is TabViewItem item) foreach (var (_, term) in TerminalsIn(item)) term.Refresh();
        }
    }

    // ---------- macros ----------

    /// <summary>Every terminal a tab holds (terminal tabs are always a SplitTabView, usually with one pane).</summary>
    private static IEnumerable<(TerminalTabViewModel Tab, TerminalControl Term)> TerminalsIn(TabViewItem item) =>
        item.Content is SplitTabView split ? split.Panes.Select(p => (p.Tab, p.Term)) : [];

    /// <summary>The terminal keyboard commands apply to: the tab's focused pane.</summary>
    private static (TerminalTabViewModel Tab, TerminalControl Term)? ActiveTerminalIn(TabViewItem? item) =>
        item?.Content is SplitTabView { ActivePane: { } pane } ? (pane.Tab, pane.Term) : null;

    private TerminalSession? SelectedSession() => ActiveTerminalIn(Tabs.SelectedItem as TabViewItem)?.Tab.Session;

    private TerminalTabViewModel? SelectedTerminalTab() => ActiveTerminalIn(Tabs.SelectedItem as TabViewItem)?.Tab;

    private IEnumerable<TerminalSession> AllSessions() => AllTerminalTabs().Select(t => t.Session);

    private void RebuildMacroMenus()
    {
        PlayMacroMenu.Items.Clear();
        DeleteMacroMenu.Items.Clear();
        if (_macros.Count == 0)
        {
            PlayMacroMenu.Items.Add(new MenuFlyoutItem { Text = "(no macros yet)", IsEnabled = false });
            DeleteMacroMenu.Items.Add(new MenuFlyoutItem { Text = "(no macros yet)", IsEnabled = false });
            return;
        }
        foreach (var m in _macros)
        {
            var play = new MenuFlyoutItem { Text = $"{m.Name}  ({m.Steps.Count} steps)" };
            play.Click += (_, _) => { PlayMacro(m); RepaintSoon(); };
            PlayMacroMenu.Items.Add(play);

            var del = new MenuFlyoutItem { Text = m.Name };
            del.Click += (_, _) => { DeleteMacro(m); RepaintSoon(); };
            DeleteMacroMenu.Items.Add(del);
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null)
        {
            var session = SelectedSession();
            if (session is null || !session.IsConnected)
            {
                await MessageAsync("Record macro", "Open a connected session tab first, then start recording.");
                return;
            }
            _recorder = new MacroRecorder();
            _recordingSession = session;
            _recordHandler = data => _recorder?.OnInput(data);
            session.InputSent += _recordHandler;
            RecordItem.Text = "Stop Recording Macro";
            UpdateStatus();
            return;
        }

        if (_recordingSession is not null && _recordHandler is not null) _recordingSession.InputSent -= _recordHandler;
        var steps = _recorder.Finish();
        _recorder = null; _recordingSession = null; _recordHandler = null;
        RecordItem.Text = "Start Recording Macro";
        UpdateStatus();

        if (steps.Count == 0) { await MessageAsync("Record macro", "Nothing was recorded."); return; }

        var box = new TextBox { PlaceholderText = "Macro name, e.g. Show interfaces" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{steps.Count} step(s) recorded. Macros are saved as plain text, so anything you typed while recording, including passwords, is stored.",
        });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Save macro",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Discard",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(box.Text)) args.Cancel = true; };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        var macro = new Macro { Name = box.Text.Trim(), Steps = steps };
        _macros.Add(macro);
        try { _macroStore.Save(_macros); }
        catch (Exception ex) { _macros.Remove(macro); await MessageAsync("Could not save macro", ex.Message); return; }
        RebuildMacroMenus();
    }

    private async void PlayMacro(Macro macro)
    {
        if (_recorder is not null) { await MessageAsync("Play macro", "Stop recording first."); return; }
        var session = SelectedSession();
        if (session is null || !session.IsConnected) { await MessageAsync("Play macro", "Select a connected session tab first."); return; }
        // Off the UI thread so the per-step delays never freeze the window.
        _ = Task.Run(async () => { try { await MacroPlayer.PlayAsync(session, macro); } catch (OperationCanceledException) { } });
    }

    private async void DeleteMacro(Macro macro)
    {
        _macros.Remove(macro);
        try { _macroStore.Save(_macros); }
        catch (Exception ex) { await MessageAsync("Could not update macros", ex.Message); }
        RebuildMacroMenus();
    }

    // ---------- session logging ----------

    private async void Log_Click(object sender, RoutedEventArgs e)
    {
        var tab = SelectedTerminalTab();
        if (tab is null || !tab.Session.IsConnected)
        {
            await MessageAsync("Log to file", "Select a connected session tab first.");
            return;
        }
        if (tab.Logger is null)
        {
            try { tab.Logger = SessionLogger.Start(tab.Session, _vm.Settings.LogDirectory, tab.Profile.Name); }
            catch (Exception ex) { await MessageAsync("Could not start logging", ex.Message); return; }
        }
        else
        {
            var path = tab.Logger.Path;
            tab.Logger.Dispose();
            tab.Logger = null;
            await MessageAsync("Logging stopped", $"Transcript saved to:\n{path}");
        }
        UpdateLogMenuState();
    }

    private async void SaveScrollback_Click(object sender, RoutedEventArgs e)
    {
        var tab = SelectedTerminalTab();
        if (tab is null) { await MessageAsync("Save scrollback", "Select a terminal session first."); return; }
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedFileName = string.Concat(tab.Profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + "-scrollback";
        picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, tab.Session.Engine.GetBufferText());
            await MessageAsync("Scrollback saved", $"Saved terminal output to:\n{file.Path}");
        }
        catch (Exception ex) { await MessageAsync("Could not save scrollback", ex.Message); }
    }

    private void UpdateLogMenuState()
    {
        var tab = SelectedTerminalTab();
        LogItem.Text = tab?.Logger is not null ? "Stop Logging to File" : "Start Logging to File";
    }

    // ---------- ssh tunnels ----------

    private async void Tunnels_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session?.Transport is not ISshTunnelHost host)
        {
            await MessageAsync("SSH Tunnels", "Select a connected built-in SSH session tab first. Tunnels are not available for Telnet, PuTTY sessions, or the Home tab.");
            return;
        }
        var dialog = new TunnelsDialog(RootGrid.XamlRoot, host);
        await ShowDialogAsync(dialog);
    }

    // ---------- sftp ----------

    private async void SftpBrowser_Click(object sender, RoutedEventArgs e)
    {
        SessionProfile? profile = SelectedTerminalTab()?.Profile ?? (Tabs.SelectedItem as TabViewItem)?.Tag as SessionProfile;
        if (profile is not { IsSshBased: true })
        {
            await MessageAsync("SFTP Browser", "Select an SSH session tab first, or right-click a saved SSH session in the sidebar.");
            return;
        }
        await OpenSftpBrowserAsync(profile);
    }

    private async Task OpenSftpBrowserAsync(SessionProfile profile)
    {
        var dialog = new SftpBrowserDialog(RootGrid.XamlRoot, _vm.Resolve(profile), _vm.Settings, _vm.SaveSettings);
        if (!await dialog.InitializeAsync()) { await ShowDialogAsync(dialog); return; }
        await ShowDialogAsync(dialog);
    }

    // ---------- voice commands ----------

    private async void VoiceCommand_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null || !session.IsConnected)
        {
            await MessageAsync("Voice Command", "Select a connected session tab first.");
            return;
        }
        var dialog = new VoiceCommandDialog(RootGrid.XamlRoot, session, _vm.Settings, _vm.SaveSettings);
        await ShowDialogAsync(dialog);
    }

    // ---------- multi-execution ----------

    /// <summary>Tabs unchecked in the target picker - empty by default, meaning "every tab" (today's original
    /// broadcast-to-all behavior), until the user narrows it down.</summary>
    private readonly HashSet<TerminalTabViewModel> _multiExecExcluded = new();

    private IEnumerable<TerminalTabViewModel> AllTerminalTabs() =>
        Tabs.TabItems.OfType<TabViewItem>().SelectMany(TerminalsIn).Select(x => x.Tab);

    private IEnumerable<TerminalSession> MultiExecTargets() =>
        AllTerminalTabs().Where(t => !_multiExecExcluded.Contains(t)).Select(t => t.Session);

    private void MultiExec_Click(object sender, RoutedEventArgs e)
    {
        bool show = MultiExecBar.Visibility != Visibility.Visible;
        MultiExecBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            UpdateMultiExecTargetsLabel();
            MultiExecCount.Text = $"{MultiExecTargets().Count(s => s.IsConnected)} connected tab(s)";
            MultiExecBox.Focus(FocusState.Programmatic);
        }
        else FocusActiveTerminal();
    }

    private void FocusActiveTerminal() => ActiveTerminalIn(Tabs.SelectedItem as TabViewItem)?.Term.Focus(FocusState.Programmatic);

    private void UpdateMultiExecTargetsLabel()
    {
        var all = AllTerminalTabs().ToList();
        int included = all.Count(t => !_multiExecExcluded.Contains(t));
        MultiExecTargetsLabel.Text = all.Count == 0 || included == all.Count ? "All tabs" : $"{included} of {all.Count} tabs";
    }

    private void MultiExecTargets_Click(object sender, RoutedEventArgs e)
    {
        var tabs = AllTerminalTabs().ToList();
        var menu = new MenuFlyout();
        if (tabs.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "(no open session tabs)", IsEnabled = false });
        }
        else
        {
            foreach (var tab in tabs)
            {
                var toggle = new ToggleMenuFlyoutItem
                {
                    Text = tab.Session.IsConnected ? tab.Profile.Name : $"{tab.Profile.Name} (not connected)",
                    IsChecked = !_multiExecExcluded.Contains(tab),
                };
                toggle.Click += (_, _) =>
                {
                    if (toggle.IsChecked) _multiExecExcluded.Remove(tab); else _multiExecExcluded.Add(tab);
                    UpdateMultiExecTargetsLabel();
                };
                menu.Items.Add(toggle);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var selectAll = new MenuFlyoutItem { Text = "Select all" };
            selectAll.Click += (_, _) => { _multiExecExcluded.Clear(); UpdateMultiExecTargetsLabel(); };
            menu.Items.Add(selectAll);
        }
        menu.ShowAt(MultiExecTargetsButton);
    }

    private void MultiExecBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        SendMultiExec();
    }

    private void MultiExecSend_Click(object sender, RoutedEventArgs e) => SendMultiExec();

    private async void Workflows_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WorkflowDialog(RootGrid.XamlRoot, _workflowStore.Load());
        if (await ShowDialogAsync(dialog) is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return;
        try { _workflowStore.Save(dialog.Workflows); }
        catch (Exception ex) { await MessageAsync("Could not save workflows", ex.Message); return; }
        if (dialog.RunWorkflow is not { } workflow) return;
        var tab = SelectedTerminalTab();
        if (tab is null || !tab.Session.IsConnected) { await MessageAsync("Run workflow", "Select a connected terminal session first."); return; }
        var preview = new StackPanel { Spacing = 8 };
        preview.Children.Add(new TextBlock { Text = $"Target: {tab.Profile.Display}\nWorkflow: {workflow.Name}\n{workflow.Description}", TextWrapping = TextWrapping.Wrap });
        preview.Children.Add(new TextBlock { Text = string.Join("\n", workflow.Commands), FontFamily = new FontFamily("Cascadia Mono"), TextWrapping = TextWrapping.Wrap });
        var confirm = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Review workflow commands", Content = preview, PrimaryButtonText = "Run on this session", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        try { await WorkflowRunner.RunAsync(tab.Session, workflow); }
        catch (Exception ex) { await MessageAsync("Workflow stopped", ex.Message); }
    }

    private async void ExportWorkflows_Click(object sender, RoutedEventArgs e)
    {
        var workflows = _workflowStore.Load();
        if (workflows.Count == 0) { await MessageAsync("Export workflows", "There are no saved workflows to export."); return; }
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedFileName = "oml-terminal-workflows";
        picker.FileTypeChoices.Add("Workflow JSON", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try { new JsonWorkflowStore(file.Path).Save(workflows); await MessageAsync("Workflows exported", $"Exported {workflows.Count} workflow(s). Review command text for embedded credentials or private addresses before sharing.\n{file.Path}"); }
        catch (Exception ex) { await MessageAsync("Could not export workflows", ex.Message); }
    }

    private async void ImportWorkflows_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var incoming = new JsonWorkflowStore(file.Path).Load();
            if (incoming.Count == 0) { await MessageAsync("Import workflows", "No valid workflows were found in that file."); return; }
            var merged = _workflowStore.Load();
            foreach (var workflow in incoming)
            {
                var existing = merged.FindIndex(w => w.Id == workflow.Id);
                if (existing >= 0) merged[existing] = workflow;
                else merged.Add(workflow);
            }
            _workflowStore.Save(merged);
            await MessageAsync("Workflows imported", $"Imported or updated {incoming.Count} workflow(s). Review commands before running them.");
        }
        catch (Exception ex) { await MessageAsync("Could not import workflows", ex.Message); }
    }

    private void SendMultiExec()
    {
        var text = MultiExecBox.Text;
        if (!string.IsNullOrEmpty(text)) _ = ConfirmAndSendMultiExecAsync(text);
    }

    private async Task ConfirmAndSendMultiExecAsync(string text)
    {
        var targets = MultiExecTargets().Where(s => s.IsConnected).ToList();
        if (targets.Count == 0) { MultiExecCount.Text = "No connected tabs"; return; }
        var names = AllTerminalTabs().Where(t => targets.Contains(t.Session)).Select(t => t.Profile.Display).Distinct().ToList();
        var preview = new StackPanel { Spacing = 8 };
        preview.Children.Add(new TextBlock { Text = $"This will send the command to {targets.Count} connected session(s):", TextWrapping = TextWrapping.Wrap });
        preview.Children.Add(new TextBlock { Text = string.Join("\n", names), FontFamily = new FontFamily("Cascadia Mono"), TextWrapping = TextWrapping.Wrap, MaxHeight = 160 });
        preview.Children.Add(new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Mono"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        var confirm = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Review multi-device command", Content = preview, PrimaryButtonText = "Send to listed sessions", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        var sent = MultiExecutor.Broadcast(targets, text);
        MultiExecCount.Text = $"Sent to {sent} session(s)";
        if (sent > 0) MultiExecBox.Text = "";
    }

    // ---------- keyword highlighting ----------

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.HighlightKeywords = HighlightItem.IsChecked;
        _vm.SaveSettings();
        var rules = _vm.Settings.HighlightKeywords ? HighlightRuleSet.Default : null;
        foreach (var (tab, term) in Tabs.TabItems.OfType<TabViewItem>().SelectMany(TerminalsIn))
        {
            tab.Session.Engine.Highlights = rules;
            term.Refresh();
        }
    }

    /// <summary>Blocks the UI behind the master-password dialog until unlocked; Exit closes the app.</summary>
    private async Task UnlockIfNeededAsync()
    {
        while (_vm.IsLocked)
        {
            var dialog = new UnlockDialog(RootGrid.XamlRoot, _vm);
            await ShowDialogAsync(dialog);
            if (_vm.IsLocked) { Close(); return; }
        }
        _ready.TrySetResult();
    }

    // ---------- session list ----------

    private void RefreshSessionList()
    {
        var (folders, unfiled) = _vm.BuildTree(FilterBox.Text);
        _folderRows.Clear();
        SessionTree.RootNodes.Clear();
        foreach (var folder in folders) SessionTree.RootNodes.Add(BuildFolderNode(folder));
        foreach (var s in unfiled) SessionTree.RootNodes.Add(new TreeViewNode { Content = BuildSessionRow(s) });
    }

    private TreeViewNode BuildFolderNode(SessionTreeFolder folder)
    {
        var node = new TreeViewNode { Content = BuildFolderRow(folder), IsExpanded = !_collapsedFolders.Contains(folder.FullPath) };
        foreach (var sub in folder.Subfolders) node.Children.Add(BuildFolderNode(sub));
        foreach (var s in folder.Sessions) node.Children.Add(new TreeViewNode { Content = BuildSessionRow(s) });
        return node;
    }

    /// <summary>Tag carries the domain object (SessionTreeFolder or SessionProfile) so TreeView-level events
    /// (which only see the row's Content as a plain UIElement) can recover it without any data-binding.</summary>
    private FrameworkElement BuildFolderRow(SessionTreeFolder folder)
    {
        var grid = new Grid { ColumnSpacing = 6, Padding = new Thickness(6, 4, 6, 4), MinWidth = 180, Tag = folder, Background = RowIdleBrush, CornerRadius = new CornerRadius(4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { Glyph = "", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current.Resources.TryGetValue("OmlSkyBrush", out var skyBrush)) icon.Foreground = (Brush)skyBrush;
        Grid.SetColumn(icon, 0);

        var text = new TextBlock
        {
            Text = folder.HeaderText,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(text, 1);

        var menuButton = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 3, 5, 3),
            Content = new FontIcon { Glyph = "", FontSize = 10 },
        };
        ToolTipService.SetToolTip(menuButton, "Folder actions");
        Grid.SetColumn(menuButton, 2);
        menuButton.Click += (_, _) => ShowFolderMenu(folder, menuButton);

        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(menuButton);

        grid.PointerEntered += (_, _) => { if (_dragSourceRow is null) grid.Background = RowHoverBrush; };
        grid.PointerExited += (_, _) => { if (_dragSourceRow is null) grid.Background = RowIdleBrush; };
        grid.RightTapped += (_, e) => ShowFolderMenu(folder, grid, e.GetPosition(grid));
        WireDragSource(grid, folder);
        _folderRows.Add(grid);

        return grid;
    }

    private FrameworkElement BuildSessionRow(SessionProfile p)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 4, 6, 4), MinWidth = 180, Tag = p, Background = RowIdleBrush, CornerRadius = new CornerRadius(4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border { Width = 46, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current.Resources.TryGetValue("BadgeBorderStyle", out var badgeStyle)) badge.Style = (Style)badgeStyle;
        var badgeText = new TextBlock { Text = p.ProtocolLabel };
        if (Application.Current.Resources.TryGetValue("BadgeTextStyle", out var badgeTextStyle)) badgeText.Style = (Style)badgeTextStyle;
        badge.Child = badgeText;
        Grid.SetColumn(badge, 0);

        var namePanel = new StackPanel();
        namePanel.Children.Add(new TextBlock { Text = p.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = string.Join(" · ", new[] { p.Host, p.Tags }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var hostText = new TextBlock { Text = details, FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis };
        if (Application.Current.Resources.TryGetValue("MonoFont", out var monoFont)) hostText.FontFamily = (FontFamily)monoFont;
        namePanel.Children.Add(hostText);
        Grid.SetColumn(namePanel, 1);

        grid.Children.Add(badge);
        grid.Children.Add(namePanel);

        grid.PointerEntered += (_, _) => { if (_dragSourceRow is null) grid.Background = RowHoverBrush; };
        grid.PointerExited += (_, _) => { if (_dragSourceRow is null) grid.Background = RowIdleBrush; };
        grid.DoubleTapped += (_, _) => OpenSession(p);
        grid.RightTapped += (_, e) => ShowSessionMenu(p, grid, e.GetPosition(grid));
        WireDragSource(grid, p);

        return grid;
    }

    // ---------- manual sidebar-tree drag-and-drop ----------
    //
    // WinUI's native TreeView drag-and-drop (CanDragItems/AllowDrop/DragItemsStarting, backed by OLE/DataPackage)
    // proved unreliable here: no visible drag feedback, hard to reliably start a drag, and drops sometimes not
    // registering. This replaces all of that with plain Pointer events: press-and-hold-and-move past a small
    // threshold starts a drag, a floating pill (DragGhost in XAML) follows the cursor, the row currently under
    // the cursor is hit-tested and highlighted directly, and the move happens on release via direct object
    // references - no DataPackage/text-serialization round trip at all.

    private void WireDragSource(Panel row, object tag)
    {
        row.PointerPressed += (_, e) => Row_PointerPressed(row, tag, e);
        row.PointerMoved += Row_PointerMoved;
        row.PointerReleased += Row_PointerReleased;
        row.PointerCanceled += Row_PointerCaptureLost;
        row.PointerCaptureLost += Row_PointerCaptureLost;
    }

    private void Row_PointerPressed(Panel row, object tag, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(row);
        if (!point.Properties.IsLeftButtonPressed) return;
        // Pointer capture is deliberately NOT taken here - only once real dragging starts, in
        // Row_PointerMoved. Capturing on every press (even a plain click with no movement) swallows this
        // row's PointerReleased before TreeView's own click-to-expand/collapse gesture ever sees it, which is
        // exactly why tapping a folder row stopped reliably expanding/collapsing it.
        _dragSourceRow = row;
        _dragSourceTag = tag;
        _dragStartPoint = e.GetCurrentPoint(RootGrid).Position;
        _dragActive = false;
    }

    private void Row_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSourceRow is null) return;
        var current = e.GetCurrentPoint(RootGrid).Position;
        if (!_dragActive)
        {
            if (Math.Abs(current.X - _dragStartPoint.X) < 6 && Math.Abs(current.Y - _dragStartPoint.Y) < 6) return;
            _dragActive = true;
            _dragSourceRow.CapturePointer(e.Pointer);
            ShowDragGhost(_dragSourceTag!);
            _dragSourceRow.Background = RowIdleBrush;
        }
        PositionDragGhost(current);
        UpdateDropTarget(current);
    }

    private void Row_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragSourceRow is null) return;
        // FinishDrag first, ReleasePointerCapture after: releasing capture synchronously raises
        // PointerCaptureLost, which would otherwise call FinishDrag(null) - clearing all the drag state -
        // before this method's own FinishDrag(point) call below ever got to run the actual move.
        var row = _dragSourceRow;
        var wasCaptured = _dragActive; // capture only ever happens once a real drag starts, in Row_PointerMoved
        FinishDrag(e.GetCurrentPoint(RootGrid).Position);
        if (wasCaptured) row.ReleasePointerCapture(e.Pointer);
    }

    private void Row_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => FinishDrag(null);

    private void FinishDrag(Windows.Foundation.Point? releasePoint)
    {
        if (_dragSourceRow is null) return;
        bool wasDragging = _dragActive;
        var sourceTag = _dragSourceTag;
        var targetFolder = (_dragHoverTarget?.Tag as SessionTreeFolder);

        HideDragGhost();
        if (_dragHoverTarget is not null) { _dragHoverTarget.Background = RowIdleBrush; _dragHoverTarget = null; }
        _dragSourceRow = null;
        _dragSourceTag = null;
        _dragActive = false;

        if (!wasDragging || sourceTag is null || releasePoint is not { } point) return;

        if (targetFolder is not null) PerformMove(sourceTag, targetFolder);
        else if (IsPointerOverTree(point)) PerformUnfileOrPromote(sourceTag);
    }

    private void ShowDragGhost(object tag)
    {
        DragGhostText.Text = tag switch
        {
            SessionTreeFolder f => f.Name,
            SessionProfile p => p.Name,
            _ => "",
        };
        DragGhost.Visibility = Visibility.Visible;
    }

    private void PositionDragGhost(Windows.Foundation.Point rootPoint)
    {
        Canvas.SetLeft(DragGhost, rootPoint.X + 16);
        Canvas.SetTop(DragGhost, rootPoint.Y + 16);
    }

    private void HideDragGhost() => DragGhost.Visibility = Visibility.Collapsed;

    /// <summary>Hit-tests the cursor (root-relative) against every currently-visible folder row to find the
    /// current drop target, replacing whichever row was previously highlighted. A row that's collapsed away
    /// (inside a collapsed ancestor folder) has zero actual size, so it's naturally skipped here.</summary>
    private void UpdateDropTarget(Windows.Foundation.Point rootPoint)
    {
        Panel? target = null;
        foreach (var row in _folderRows)
        {
            if (ReferenceEquals(row, _dragSourceRow)) continue;
            if (row.ActualWidth <= 0 || row.ActualHeight <= 0) continue;
            var topLeft = row.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
            var rect = new Windows.Foundation.Rect(topLeft.X, topLeft.Y, row.ActualWidth, row.ActualHeight);
            if (rect.Contains(rootPoint)) { target = row; break; }
        }
        if (ReferenceEquals(_dragHoverTarget, target)) return;
        if (_dragHoverTarget is not null) _dragHoverTarget.Background = RowIdleBrush;
        _dragHoverTarget = target;
        if (_dragHoverTarget is not null) _dragHoverTarget.Background = RowDragOverBrush;
    }

    private bool IsPointerOverTree(Windows.Foundation.Point rootPoint)
    {
        var topLeft = SessionTree.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
        var rect = new Windows.Foundation.Rect(topLeft.X, topLeft.Y, SessionTree.ActualWidth, SessionTree.ActualHeight);
        return rect.Contains(rootPoint);
    }

    /// <summary>Moves whatever was dragged (a session or a whole folder) so it lands inside targetFolder.</summary>
    private void PerformMove(object sourceTag, SessionTreeFolder targetFolder)
    {
        if (sourceTag is SessionProfile p)
        {
            if (string.Equals(p.Folder, targetFolder.FullPath, StringComparison.OrdinalIgnoreCase)) return;
            MoveToFolder(p, targetFolder.FullPath);
        }
        else if (sourceTag is SessionTreeFolder folder)
        {
            if (string.Equals(folder.FullPath, targetFolder.FullPath, StringComparison.OrdinalIgnoreCase)) return;
            // Dropping a folder onto itself or one of its own descendants would create a cycle.
            if (targetFolder.FullPath.StartsWith(folder.FullPath + "/", StringComparison.OrdinalIgnoreCase)) return;
            var newPath = $"{targetFolder.FullPath}/{folder.Name}";
            if (string.Equals(newPath, folder.FullPath, StringComparison.OrdinalIgnoreCase)) return;
            _vm.RenameFolder(folder.FullPath, newPath);
            RefreshSessionList();
        }
    }

    /// <summary>Dropped in the tree's empty background, not on any folder row: unfiles a session or promotes
    /// a folder back to the top level, MobaXterm/SecureCRT-style.</summary>
    private void PerformUnfileOrPromote(object sourceTag)
    {
        if (sourceTag is SessionProfile p)
        {
            if (string.IsNullOrEmpty(p.Folder)) return;
            MoveToFolder(p, "");
        }
        else if (sourceTag is SessionTreeFolder folder)
        {
            if (!folder.FullPath.Contains('/')) return;
            var name = folder.FullPath[(folder.FullPath.LastIndexOf('/') + 1)..];
            _vm.RenameFolder(folder.FullPath, name);
            RefreshSessionList();
        }
    }

    private void SessionTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is FrameworkElement { Tag: SessionTreeFolder folder }) _collapsedFolders.Remove(folder.FullPath);
    }

    private void SessionTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Node.Content is FrameworkElement { Tag: SessionTreeFolder folder }) _collapsedFolders.Add(folder.FullPath);
    }

    /// <summary>TreeView already expands/collapses a folder row natively on tap (chevron or row body alike);
    /// only a session row needs a custom action here.</summary>
    private void SessionTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FrameworkElement { Tag: SessionProfile p }) OpenSession(p);
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => RefreshSessionList();

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(true);
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(false);

    private void SetAllExpanded(bool expanded)
    {
        void Walk(TreeViewNode node)
        {
            if (node.Content is FrameworkElement { Tag: SessionTreeFolder folder })
            {
                node.IsExpanded = expanded;
                if (expanded) _collapsedFolders.Remove(folder.FullPath); else _collapsedFolders.Add(folder.FullPath);
            }
            foreach (var child in node.Children) Walk(child);
        }
        foreach (var root in SessionTree.RootNodes) Walk(root);
    }

    private void SessionTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var tag = (SessionTree.SelectedNode?.Content as FrameworkElement)?.Tag;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter when tag is SessionProfile p:
                e.Handled = true;
                OpenSession(p);
                break;
            case Windows.System.VirtualKey.Delete when tag is SessionProfile p:
                e.Handled = true;
                _vm.Remove(p);
                break;
            case Windows.System.VirtualKey.Delete when tag is SessionTreeFolder folder:
                e.Handled = true;
                _ = DeleteFolderAsync(folder);
                break;
            case Windows.System.VirtualKey.F2 when tag is SessionProfile p:
                e.Handled = true;
                _ = EditSessionAsync(p);
                break;
            case Windows.System.VirtualKey.F2 when tag is SessionTreeFolder folder:
                e.Handled = true;
                _ = RenameFolderAsync(folder);
                break;
        }
    }

    private void ShowSessionMenu(SessionProfile p, FrameworkElement target, Windows.Foundation.Point at)
    {
        var menu = new MenuFlyout();
        var connect = new MenuFlyoutItem { Text = "Connect" };
        connect.Click += (_, _) => OpenSession(p);
        var edit = new MenuFlyoutItem { Text = "Edit..." };
        edit.Click += async (_, _) => await EditSessionAsync(p);
        var delete = new MenuFlyoutItem { Text = "Delete" };
        delete.Click += (_, _) => _vm.Remove(p);
        var duplicate = new MenuFlyoutItem { Text = "Duplicate" };
        duplicate.Click += (_, _) =>
        {
            var copy = p.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = $"{p.Name} (copy)";
            try { _vm.Add(copy); } catch (SessionValidationException ex) { _ = MessageAsync("Could not duplicate", string.Join("\n", ex.Errors)); }
        };
        menu.Items.Add(connect);
        if (ActiveTerminalIn(Tabs.SelectedItem as TabViewItem) is not null)
        {
            var splitHere = new MenuFlyoutItem { Text = "Open in Split (right)" };
            splitHere.Click += (_, _) => SplitWith(p, Orientation.Horizontal);
            menu.Items.Add(splitHere);
        }
        menu.Items.Add(edit);
        menu.Items.Add(duplicate);
        menu.Items.Add(BuildMoveToFolderMenu(p));
        if (p.IsSshBased)
        {
            var sftp = new MenuFlyoutItem { Text = "SFTP Browser..." };
            sftp.Click += async (_, _) => await OpenSftpBrowserAsync(p);
            menu.Items.Add(sftp);
        }
        if (!string.IsNullOrWhiteSpace(p.Host))
        {
            var tools = new MenuFlyoutSubItem { Text = "Tools" };
            var portQuery = new MenuFlyoutItem { Text = $"Port Query {p.Host}" };
            portQuery.Click += (_, _) => OpenTool("portscan", v => ((PortScanView)v).SetTarget(p.Host));
            var ping = new MenuFlyoutItem { Text = $"Ping {p.Host}" };
            ping.Click += (_, _) => OpenTool("ping", v => ((PingTraceView)v).SetTarget(p.Host));
            var trace = new MenuFlyoutItem { Text = $"Traceroute {p.Host}" };
            trace.Click += (_, _) => OpenTool("ping", v => ((PingTraceView)v).SetTarget(p.Host, 1));
            tools.Items.Add(portQuery);
            tools.Items.Add(ping);
            tools.Items.Add(trace);
            if (p.IsSshBased)
            {
                var backup = new MenuFlyoutItem { Text = "Back up config..." };
                backup.Click += (_, _) => OpenTool("backup");
                var capture = new MenuFlyoutItem { Text = "Packet capture..." };
                capture.Click += (_, _) => OpenTool("capture");
                tools.Items.Add(new MenuFlyoutSeparator());
                tools.Items.Add(backup);
                tools.Items.Add(capture);
            }
            menu.Items.Add(tools);
        }
        if (p.IsSshBased && !string.IsNullOrWhiteSpace(p.Host))
        {
            var forget = new MenuFlyoutItem { Text = "Forget saved host key" };
            forget.Click += (_, _) =>
            {
                int n = Core.Ssh.HostKeyVerifier.Default.Forget(p.Host, p.Port);
                if (p.UseJumpHost && !string.IsNullOrWhiteSpace(p.JumpHost)) n += Core.Ssh.HostKeyVerifier.Default.Forget(p.JumpHost, p.JumpPort);
                _ = MessageAsync("Host key", n > 0
                    ? $"Forgot the saved host key for {p.Host}:{p.Port}{(p.UseJumpHost ? $" and its jump host {p.JumpHost}:{p.JumpPort}" : "")}. The next connection will save the key offered then."
                    : $"No host key was saved for {p.Host}:{p.Port}.");
            };
            menu.Items.Add(forget);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(delete);
        menu.ShowAt(target, at);
    }

    private MenuFlyoutSubItem BuildMoveToFolderMenu(SessionProfile p)
    {
        var moveTo = new MenuFlyoutSubItem { Text = "Move to folder" };

        var noFolder = new MenuFlyoutItem { Text = "(No folder)", IsEnabled = !string.IsNullOrWhiteSpace(p.Folder) };
        noFolder.Click += (_, _) => MoveToFolder(p, "");
        moveTo.Items.Add(noFolder);

        var folders = ExistingFolders().Where(f => !string.IsNullOrWhiteSpace(f)).Distinct()
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count > 0) moveTo.Items.Add(new MenuFlyoutSeparator());
        foreach (var folder in folders)
        {
            var item = new MenuFlyoutItem { Text = folder, IsEnabled = folder != p.Folder };
            item.Click += (_, _) => MoveToFolder(p, folder);
            moveTo.Items.Add(item);
        }

        moveTo.Items.Add(new MenuFlyoutSeparator());
        var newFolder = new MenuFlyoutItem { Text = "New folder..." };
        newFolder.Click += async (_, _) => await MoveToNewFolderAsync(p);
        moveTo.Items.Add(newFolder);

        return moveTo;
    }

    private void MoveToFolder(SessionProfile p, string folder)
    {
        var updated = p.Clone();
        updated.Folder = folder;
        try { _vm.Replace(p, updated); }
        catch (SessionValidationException ex) { _ = MessageAsync("Could not move session", string.Join("\n", ex.Errors)); }
    }

    private async Task MoveToNewFolderAsync(SessionProfile p)
    {
        var box = new TextBox { PlaceholderText = "Folder name", Text = p.Folder };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = "New folder",
            Content = box,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(box.Text)) args.Cancel = true; };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) MoveToFolder(p, box.Text.Trim());
    }

    // ---------- command palette ----------

    private void GlobalSearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenTool("search", v => ((GlobalSearchView)v).FocusSearch());
    }

    private void PaletteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        TogglePalette();
    }

    private void TogglePalette()
    {
        if (PaletteScrim.Visibility == Visibility.Visible) ClosePalette();
        else OpenPalette();
    }

    private void OpenPalette()
    {
        PaletteQuery.Text = "";
        RefreshPaletteResults();
        PaletteScrim.Visibility = Visibility.Visible;
        PaletteQuery.Focus(FocusState.Programmatic);
    }

    private void ClosePalette() => PaletteScrim.Visibility = Visibility.Collapsed;

    private void PaletteScrim_Tapped(object sender, TappedRoutedEventArgs e) => ClosePalette();
    private void PaletteCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void PaletteQuery_TextChanged(object sender, TextChangedEventArgs e) => RefreshPaletteResults();

    private void PaletteQuery_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                ClosePalette();
                break;
            case Windows.System.VirtualKey.Down:
                e.Handled = true;
                if (PaletteResults.Items.Count > 0)
                    PaletteResults.SelectedIndex = Math.Min(PaletteResults.SelectedIndex + 1, PaletteResults.Items.Count - 1);
                break;
            case Windows.System.VirtualKey.Up:
                e.Handled = true;
                if (PaletteResults.Items.Count > 0)
                    PaletteResults.SelectedIndex = Math.Max(PaletteResults.SelectedIndex - 1, 0);
                break;
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                var chosen = PaletteResults.SelectedItem as PaletteItem ?? PaletteResults.Items.OfType<PaletteItem>().FirstOrDefault();
                if (chosen is not null) ExecutePaletteItem(chosen);
                break;
        }
    }

    private void PaletteResults_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteItem item) ExecutePaletteItem(item);
    }

    private void ExecutePaletteItem(PaletteItem item)
    {
        ClosePalette();
        item.Execute();
    }

    private void RefreshPaletteResults()
    {
        var items = BuildPaletteItems(PaletteQuery.Text);
        PaletteResults.ItemsSource = items;
        if (items.Count > 0) PaletteResults.SelectedIndex = 0;
    }

    private List<PaletteItem> BuildPaletteItems(string query)
    {
        var actions = GetPaletteActions();
        var q = query.Trim();
        if (q.Length == 0)
        {
            var recent = _vm.Recent().Take(5).Select(SessionPaletteItem);
            return recent.Concat(actions).ToList();
        }

        bool Match(string s) => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var sessions = _vm.Sessions
            .Where(s => Match(s.Name) || Match(s.Host) || Match(s.Folder) || Match(s.Tags) || Match(s.ProtocolLabel))
            .Select(SessionPaletteItem);
        var matchedActions = actions.Where(a => Match(a.Title));

        return sessions.Concat(matchedActions)
            .OrderBy(i => i.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    private PaletteItem SessionPaletteItem(SessionProfile s) => new()
    {
        Title = s.Name,
        Subtitle = string.Join(" · ", new[] { s.Folder, s.Host, s.Tags }.Where(x => !string.IsNullOrWhiteSpace(x))),
        Badge = s.ProtocolLabel,
        Execute = () => OpenSession(s),
    };

    /// <summary>Hand-built rather than reflected from a command registry - the app has none, and the action
    /// count is small enough that this stays easier to read than adding one just for the palette.</summary>
    private List<PaletteItem> GetPaletteActions()
    {
        var list = new List<PaletteItem>
        {
            new() { Title = "New Session", Subtitle = "Create a saved SSH / Telnet / RDP / VNC / Serial session", Shortcut = "Ctrl+N", Badge = "ACTION", Execute = () => _ = NewSessionAsync() },
            new() { Title = "Connect to OML Node...", Subtitle = "Connect via an OML Labs launch link", Shortcut = "Ctrl+Shift+O", Badge = "ACTION", Execute = () => _ = OmlNodeAsync() },
            new() { Title = "Settings...", Subtitle = "Font, scrollback, PuTTY path, master password", Badge = "ACTION", Execute = () => Settings_Click(this, new RoutedEventArgs()) },
            new() { Title = "Close Tab", Subtitle = "Close the active session tab", Shortcut = "Ctrl+W", Badge = "ACTION", Execute = () => CloseTab_Click(this, new RoutedEventArgs()) },
            new() { Title = "SSH Tunnels...", Subtitle = "Manage port forwards on the active SSH tab", Shortcut = "Ctrl+Shift+T", Badge = "ACTION", Execute = () => Tunnels_Click(this, new RoutedEventArgs()) },
            new() { Title = "SFTP Browser...", Subtitle = "Browse files on the active SSH session", Shortcut = "Ctrl+Shift+F", Badge = "ACTION", Execute = () => SftpBrowser_Click(this, new RoutedEventArgs()) },
            new() { Title = "Voice Command...", Subtitle = "Speak or type a command to send", Shortcut = "Ctrl+Shift+V", Badge = "ACTION", Execute = () => VoiceCommand_Click(this, new RoutedEventArgs()) },
            new() { Title = "Troubleshooting Workflows...", Subtitle = "Create or run a reviewed command sequence on the active session", Badge = "ACTION", Execute = () => Workflows_Click(this, new RoutedEventArgs()) },
            new() { Title = "Multi-Execution Bar", Subtitle = "Send one command to every connected tab", Shortcut = "Ctrl+Shift+M", Badge = "ACTION", Execute = () => MultiExec_Click(this, new RoutedEventArgs()) },
            new() { Title = _recorder is null ? "Start Recording Macro" : "Stop Recording Macro", Subtitle = "Record keystrokes on the active session as a macro", Shortcut = "Ctrl+Shift+R", Badge = "ACTION", Execute = () => Record_Click(this, new RoutedEventArgs()) },
            new() { Title = "Zoom In", Subtitle = "Increase terminal font size", Shortcut = "Ctrl+=", Badge = "ACTION", Execute = () => ZoomIn_Click(this, new RoutedEventArgs()) },
            new() { Title = "Zoom Out", Subtitle = "Decrease terminal font size", Shortcut = "Ctrl+-", Badge = "ACTION", Execute = () => ZoomOut_Click(this, new RoutedEventArgs()) },
            new() { Title = "Reset Zoom", Subtitle = "Restore the default terminal font size", Shortcut = "Ctrl+0", Badge = "ACTION", Execute = () => ZoomReset_Click(this, new RoutedEventArgs()) },
            new() { Title = "Toggle Keyword Highlighting", Subtitle = "Highlight errors, warnings and prompts in terminal output", Badge = "ACTION", Execute = () => { HighlightItem.IsChecked = !HighlightItem.IsChecked; Highlight_Click(this, new RoutedEventArgs()); } },
            new() { Title = "Find in Terminal", Subtitle = "Search the active session's output, scrollback included", Shortcut = "Ctrl+F", Badge = "ACTION", Execute = OpenFindBar },
            new() { Title = "Save Scrollback Snapshot...", Subtitle = "Export the active session's full terminal buffer as text", Badge = "ACTION", Execute = () => SaveScrollback_Click(this, new RoutedEventArgs()) },
            new() { Title = "About OML Terminal", Subtitle = "Version and licensing info", Badge = "ACTION", Execute = () => About_Click(this, new RoutedEventArgs()) },
            new() { Title = "Split Right...", Subtitle = "Open another session beside the active terminal", Shortcut = "Ctrl+Shift+D", Badge = "SPLIT", Execute = () => SplitRight_Click(this, new RoutedEventArgs()) },
            new() { Title = "Split Down...", Subtitle = "Open another session below the active terminal", Shortcut = "Ctrl+Shift+E", Badge = "SPLIT", Execute = () => SplitDown_Click(this, new RoutedEventArgs()) },
            new() { Title = "Toggle Synchronized Typing", Subtitle = "Type into every pane of the current split at once", Shortcut = "Ctrl+Shift+I", Badge = "SPLIT", Execute = () => { SyncItem.IsChecked = !SyncItem.IsChecked; Sync_Click(this, new RoutedEventArgs()); } },
            new() { Title = "Local Shell", Subtitle = "PowerShell / cmd / WSL / Cygwin on this PC", Badge = "SHELL", Execute = OpenDefaultLocalShell },
            new() { Title = _xserver.OwnsProcess ? "Stop X Server" : "Start X Server", Subtitle = "Local display for X11-forwarded apps (VcXsrv / Xming / Cygwin/X)", Badge = "X11", Execute = () => XServer_Click(this, new RoutedEventArgs()) },
        };
        foreach (var tool in ToolCatalog.All)
        {
            var t = tool;
            list.Add(new PaletteItem { Title = t.Title, Subtitle = t.Subtitle, Badge = "TOOL", Execute = () => OpenTool(t.Id) });
        }
        foreach (var m in _macros)
        {
            var macro = m;
            list.Add(new PaletteItem { Title = $"Play Macro: {macro.Name}", Subtitle = $"{macro.Steps.Count} recorded step(s)", Badge = "MACRO", Execute = () => { PlayMacro(macro); RepaintSoon(); } });
        }
        return list;
    }

    // ---------- find in terminal ----------

    private List<TerminalMatch> _findMatches = new();
    private int _findIndex = -1;
    private TerminalControl? _findTarget;

    private void FindAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenFindBar();
    }

    private void OpenFindBar()
    {
        if (ActiveTerminalIn(Tabs.SelectedItem as TabViewItem) is not { Term: var term }) return;
        _findTarget = term;
        FindBar.Visibility = Visibility.Visible;
        FindQuery.Text = "";
        FindCount.Text = "";
        FindQuery.Focus(FocusState.Programmatic);
    }

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _findTarget?.SetFindHighlight(null);
        _findTarget = null;
        _findMatches = new();
        _findIndex = -1;
        FocusActiveTerminal();
    }

    private void RunFind()
    {
        if (_findTarget is null) return;
        var query = FindQuery.Text;
        _findMatches = string.IsNullOrEmpty(query) ? new() : _findTarget.FindText(query, matchCase: false).ToList();
        _findIndex = _findMatches.Count > 0 ? 0 : -1;
        UpdateFindUi();
    }

    private void UpdateFindUi()
    {
        if (_findMatches.Count == 0)
        {
            FindCount.Text = string.IsNullOrEmpty(FindQuery.Text) ? "" : "No matches";
            _findTarget?.SetFindHighlight(null);
            return;
        }
        FindCount.Text = $"{_findIndex + 1} / {_findMatches.Count}";
        _findTarget?.SetFindHighlight(_findMatches[_findIndex]);
    }

    private void JumpMatch(int delta)
    {
        if (_findMatches.Count == 0) return;
        _findIndex = ((_findIndex + delta) % _findMatches.Count + _findMatches.Count) % _findMatches.Count;
        UpdateFindUi();
    }

    private void FindQuery_TextChanged(object sender, TextChangedEventArgs e) => RunFind();

    private void FindQuery_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFindBar();
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            JumpMatch(shift ? -1 : 1);
        }
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e) => JumpMatch(-1);
    private void FindNext_Click(object sender, RoutedEventArgs e) => JumpMatch(1);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    // ---------- dialogs ----------

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; RepaintSoon(); }
    }

    private IEnumerable<string> ExistingFolders() => _vm.AllFolders();

    private async Task NewSessionAsync(SessionProfile? prefill = null, string? folder = null)
    {
        var dialog = new NewSessionDialog(RootGrid.XamlRoot, existingFolders: ExistingFolders(), initialFolder: folder, credentials: _vm.Credentials.ToList());
        if (prefill is not null) dialog.Prefill(prefill);
        var result = await ShowDialogAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary) || dialog.Result is not { } profile) return;

        try { _vm.Add(profile); }
        catch (SessionValidationException ex) { await MessageAsync("Could not save session", string.Join("\n", ex.Errors)); return; }
        if (result == ContentDialogResult.Primary) OpenSession(profile);
    }

    // ---------- folder management ----------

    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync();

    private async Task CreateFolderAsync()
    {
        var box = new TextBox { PlaceholderText = "Folder name" };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = "New folder",
            Content = box,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(box.Text)) args.Cancel = true; };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        _vm.CreateFolder(box.Text.Trim());
        RefreshSessionList();
    }

    private async Task CreateSubfolderAsync(SessionTreeFolder parent)
    {
        var box = new TextBox { PlaceholderText = "Subfolder name" };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = $"New subfolder in \"{parent.Name}\"",
            Content = box,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(box.Text)) args.Cancel = true; };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        _vm.CreateFolder($"{parent.FullPath}/{box.Text.Trim()}");
        RefreshSessionList();
    }

    /// <summary>Renames just this folder's own segment, preserving its position under any parent path
    /// (e.g. renaming "Action" inside "Home/Movies/Action" keeps it nested under "Home/Movies").</summary>
    private async Task RenameFolderAsync(SessionTreeFolder folder)
    {
        var box = new TextBox { PlaceholderText = "Folder name", Text = folder.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = $"Rename \"{folder.Name}\"",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(box.Text)) args.Cancel = true; };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var newName = box.Text.Trim();
        int slash = folder.FullPath.LastIndexOf('/');
        var newFullPath = slash < 0 ? newName : $"{folder.FullPath[..(slash + 1)]}{newName}";
        _vm.RenameFolder(folder.FullPath, newFullPath);
        RefreshSessionList();
    }

    private async Task DeleteFolderAsync(SessionTreeFolder folder)
    {
        int totalCount = folder.TotalCount;
        CheckBox? alsoDelete = null;
        object content;
        if (totalCount > 0)
        {
            alsoDelete = new CheckBox { Content = $"Also delete the {totalCount} session(s) in this folder and its subfolders (otherwise they move to (No folder))" };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = $"Delete folder \"{folder.Name}\"{(folder.Subfolders.Count > 0 ? " and its subfolders" : "")}?", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(alsoDelete);
            content = panel;
        }
        else
        {
            content = new TextBlock { Text = $"Delete empty folder \"{folder.Name}\"?", TextWrapping = TextWrapping.Wrap };
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = "Delete folder",
            Content = content,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        _vm.DeleteFolder(folder.FullPath, alsoDelete?.IsChecked == true);
        RefreshSessionList();
    }

    private void ShowFolderMenu(SessionTreeFolder folder, FrameworkElement target, Windows.Foundation.Point? at = null)
    {
        var menu = new MenuFlyout();
        var newSession = new MenuFlyoutItem { Text = "New Session Here..." };
        newSession.Click += async (_, _) => await NewSessionAsync(folder: folder.FullPath);
        var newSubfolder = new MenuFlyoutItem { Text = "New Subfolder..." };
        newSubfolder.Click += async (_, _) => await CreateSubfolderAsync(folder);
        var rename = new MenuFlyoutItem { Text = "Rename Folder..." };
        rename.Click += async (_, _) => await RenameFolderAsync(folder);
        var delete = new MenuFlyoutItem { Text = "Delete Folder..." };
        delete.Click += async (_, _) => await DeleteFolderAsync(folder);
        menu.Items.Add(newSession);
        menu.Items.Add(newSubfolder);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        if (at is { } p) menu.ShowAt(target, p); else menu.ShowAt(target);
    }


    private async void ImportPutty_Click(object sender, RoutedEventArgs e)
    {
        var found = PuttySessionImporter.FindSessions();
        var dialog = new ImportPuttyDialog(RootGrid.XamlRoot, found);
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Result.Count == 0) return;

        int added = 0, failed = 0;
        foreach (var profile in dialog.Result)
        {
            try { _vm.Add(profile); added++; }
            catch (SessionValidationException) { failed++; }
        }
        await MessageAsync("Import from PuTTY", failed > 0
            ? $"Imported {added} session(s); {failed} could not be added."
            : $"Imported {added} session(s).");
    }

    private async void ImportMobaXterm_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".ini", ".mxtsessions", ".mobaconf", ".txt" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        List<ImportResult> found;
        try { found = MobaXtermImporter.Parse(await File.ReadAllTextAsync(file.Path)); }
        catch (ImportException ex) { await MessageAsync("Import from MobaXterm", ex.Message); return; }
        catch (Exception ex) { await MessageAsync("Could not read file", ex.Message); return; }
        await FinishImportAsync(found, "MobaXterm", System.IO.Path.GetFileName(file.Path));
    }

    private async void ImportSecureCrt_Click(object sender, RoutedEventArgs e)
    {
        // SecureCRT stores each session as an .ini under Config\Sessions; ask for that folder (and any config passphrase).
        var passBox = new PasswordBox { Header = "Configuration Passphrase (leave blank if none is set)" };
        var intro = new StackPanel { Spacing = 10, MinWidth = 380 };
        intro.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Pick your SecureCRT \"Sessions\" folder next (usually Documents\\...\\VanDyke\\Config\\Sessions). If you set a Configuration Passphrase in SecureCRT, enter it so saved passwords can be decrypted." });
        intro.Children.Add(passBox);
        var ask = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = "Import from SecureCRT",
            Content = intro,
            PrimaryButtonText = "Choose folder...",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(ask) != ContentDialogResult.Primary) return;

        var folder = await ToolUiPickFolderAsync();
        if (folder is null) return;
        List<ImportResult> found;
        try { found = SecureCrtImporter.ScanDirectory(folder, passBox.Password); }
        catch (Exception ex) { await MessageAsync("Could not read SecureCRT sessions", ex.Message); return; }
        await FinishImportAsync(found, "SecureCRT", System.IO.Path.GetFileName(folder));
    }

    private static async Task<string?> ToolUiPickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    /// <summary>Shows the import preview for parsed sessions and adds the chosen ones, reporting any password notes.</summary>
    private async Task FinishImportAsync(List<ImportResult> found, string source, string sourceName)
    {
        if (found.Count == 0) { await MessageAsync($"Import from {source}", "No sessions were found."); return; }
        var dialog = new ImportSessionsDialog(RootGrid.XamlRoot, found.Select(f => f.Profile).ToList(), sourceName);
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Result.Count == 0) return;

        int added = 0, failed = 0;
        foreach (var profile in dialog.Result)
        {
            try { _vm.Add(profile); added++; }
            catch (SessionValidationException) { failed++; }
        }
        int notes = found.Count(f => f.Note is not null);
        var msg = new System.Text.StringBuilder($"Imported {added} session(s) from {source}.");
        if (failed > 0) msg.Append($"\n{failed} could not be added.");
        if (notes > 0) msg.Append($"\n{notes} session(s) need their password re-entered (saved passwords couldn't be brought over). Set them in the session editor or link a Password Manager credential.");
        await MessageAsync($"Import from {source}", msg.ToString());
    }

    private async void ImportSessions_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".csv");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        List<SessionProfile> found;
        try
        {
            found = file.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? SessionCsv.Import(await File.ReadAllTextAsync(file.Path))
                : new JsonSessionStore(file.Path).Load();
        }
        catch (Exception ex) { await MessageAsync("Could not read file", ex.Message); return; }

        var dialog = new ImportSessionsDialog(RootGrid.XamlRoot, found, System.IO.Path.GetFileName(file.Path));
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Result.Count == 0) return;

        int added = 0, failed = 0;
        foreach (var profile in dialog.Result)
        {
            try { _vm.Add(profile); added++; }
            catch (SessionValidationException) { failed++; }
        }
        await MessageAsync("Import Sessions", failed > 0
            ? $"Imported {added} session(s); {failed} could not be added."
            : $"Imported {added} session(s).");
    }

    private async void ImportCredentials_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ImportCredentialsDialog(RootGrid.XamlRoot, _vm.Sessions.ToList());
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Result.Count == 0) return;

        foreach (var credential in dialog.Result) _vm.SaveCredential(credential);

        var linkResult = CredentialListImporter.LinkByUsername(_vm.Sessions, dialog.Result);
        foreach (var credentialId in linkResult.LinkedCounts.Keys)
        {
            var targets = _vm.Sessions.Where(s => s.CredentialId == credentialId).ToList();
            _vm.AssignCredential(credentialId, targets);
        }

        var linkedSessions = linkResult.LinkedCounts.Values.Sum();
        var msg = new System.Text.StringBuilder($"Imported {dialog.Result.Count} credential(s) into the vault.");
        if (linkedSessions > 0) msg.Append($"\nLinked {linkedSessions} session(s) automatically by matching username.");
        if (linkResult.AmbiguousUsernames.Count > 0)
            msg.Append($"\n{linkResult.AmbiguousUsernames.Count} username(s) matched more than one imported credential and were left for you to link by hand in the session editor: {string.Join(", ", linkResult.AmbiguousUsernames)}.");
        await MessageAsync("Import Credentials", msg.ToString());
    }

    private async void ExportSessions_Click(object sender, RoutedEventArgs e)
    {
        var includeBox = new CheckBox { Content = "Include passwords in the exported file (stored as plain text - handle the file carefully)" };
        var confirm = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = "Export Sessions",
            Content = includeBox,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        bool includeSecrets = includeBox.IsChecked == true;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedFileName = "oml-terminal-sessions";
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            if (file.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(file.Path, SessionCsv.Export(_vm.Sessions, includeSecrets));
            }
            else
            {
                var toExport = _vm.Sessions.Select(s =>
                {
                    return includeSecrets ? s.Clone() : ShareSafeCopy(s);
                });
                new JsonSessionStore(file.Path).Save(toExport);
            }
            await MessageAsync("Export complete", $"Saved {_vm.Sessions.Count} session(s) to:\n{file.Path}");
        }
        catch (Exception ex) { await MessageAsync("Could not export sessions", ex.Message); }
    }

    private static SessionProfile ShareSafeCopy(SessionProfile profile)
    {
        var copy = profile.Clone();
        copy.Password = "";
        copy.EnablePassword = "";
        copy.PrivateKeyPassphrase = "";
        copy.PrivateKeyPath = "";
        copy.JumpPassword = "";
        copy.CredentialId = null;
        copy.StartupCommands = "";
        copy.Notes = "";
        return copy;
    }

    private async void ShareConfig_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedFileName = "oml-terminal-shareable-config";
        picker.FileTypeChoices.Add("Shareable JSON (secrets removed)", new List<string> { ".json" });
        picker.FileTypeChoices.Add("Shareable CSV (secrets removed)", new List<string> { ".csv" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            if (file.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(file.Path, SessionCsv.Export(_vm.Sessions, includeSecrets: false));
            else
                new JsonSessionStore(file.Path).Save(_vm.Sessions.Select(ShareSafeCopy));
            await MessageAsync("Shareable config exported", $"Saved {_vm.Sessions.Count} profiles without passwords, credential links, local key paths, startup commands or notes. Host names and usernames remain in the file; share it only with the intended team.\n{file.Path}");
        }
        catch (Exception ex) { await MessageAsync("Could not export config", ex.Message); }
    }

    private async Task EditSessionAsync(SessionProfile p)
    {
        var dialog = new NewSessionDialog(RootGrid.XamlRoot, p, ExistingFolders(), credentials: _vm.Credentials.ToList());
        var result = await ShowDialogAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary) || dialog.Result is not { } updated) return;
        try { _vm.Replace(p, updated); }
        catch (SessionValidationException ex) { await MessageAsync("Could not save session", string.Join("\n", ex.Errors)); return; }
        if (result == ContentDialogResult.Primary) OpenSession(updated);
    }

    /// <summary>Shows the OML-node confirmation dialog. A link from outside the app is never opened without this step.</summary>
    public async Task OmlNodeAsync(string? link = null)
    {
        await _ready.Task; // wait for the master-password unlock (and for the window to be loaded)
        var dialog = new OmlNodeDialog(RootGrid.XamlRoot, link);
        var result = await ShowDialogAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return;

        if (dialog.LabLink is { } labLink)
        {
            if (result == ContentDialogResult.Primary) await BrowseLabNodesAsync(labLink);
            return;
        }
        if (dialog.Result is not { } profile) return;

        if (result == ContentDialogResult.Secondary)
        {
            try { _vm.Add(profile); }
            catch (SessionValidationException ex) { await MessageAsync("Could not save session", string.Join("\n", ex.Errors)); }
        }
        OpenSession(profile);
    }

    /// <summary>Opens (or focuses, if already open) a persistent tab listing this lab's nodes with live status, polling every few seconds. Double-click connects one node; "Connect All Online" opens every running, reachable node at once.</summary>
    private async Task BrowseLabNodesAsync(OmlLabLink labLink)
    {
        var existing = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Content is OmlLabTabView v && v.Link.LabId == labLink.LabId);
        if (existing is not null) { Tabs.SelectedItem = existing; return; }

        var labView = new OmlLabTabView(labLink);
        var item = new TabViewItem { Header = labLink.LabName, Content = labView, Tag = labView };
        labView.NodeActivated += node => OpenOmlLabNode(labLink, node);
        labView.ConnectAllRequested += async nodes => await ConnectAllAsync(labLink, nodes);

        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
        await Task.CompletedTask;
    }

    /// <summary>Opens every given node with a short stagger between each, so RDP/VNC/terminal windows don't all pop at once.</summary>
    private async Task ConnectAllAsync(OmlLabLink labLink, IReadOnlyList<OmlLabNode> nodes)
    {
        foreach (var node in nodes)
        {
            OpenOmlLabNode(labLink, node);
            await Task.Delay(250);
        }
    }

    /// <summary>Opens the right kind of connection for one OML Labs node, based on its console_type. RDP and
    /// Telnet console types are reachable as plain TCP ports on omlHost, so they reuse the existing transports
    /// unchanged; VNC has no plain-TCP option on this platform and goes through the WebSocket console proxy.</summary>
    private void OpenOmlLabNode(OmlLabLink labLink, OmlLabNode node)
    {
        var omlHostName = new Uri(labLink.OmlHost).Host;
        switch (node.ConsoleType)
        {
            case OmlConsoleType.Rdp when node.RdpPort is { } rdpPort:
                OpenSession(new SessionProfile { Name = $"{labLink.LabName}/{node.Name}", Folder = "OML Labs", Protocol = ProtocolKind.Rdp, Host = omlHostName, Port = rdpPort });
                break;
            case OmlConsoleType.Telnet or OmlConsoleType.Ssh when node.ConsolePort is { } consolePort:
                OpenSession(new SessionProfile
                {
                    Name = $"{labLink.LabName}/{node.Name}", Folder = "OML Labs",
                    Protocol = node.ConsoleType == OmlConsoleType.Ssh ? ProtocolKind.Ssh : ProtocolKind.Telnet,
                    Host = omlHostName, Port = consolePort, IsLabNode = true,
                });
                break;
            case OmlConsoleType.Vnc when node.VncWsPort is not null:
                OpenOmlVncSession(labLink, node);
                break;
            default:
                _ = MessageAsync("Cannot connect", $"{node.Name} isn't running yet, so its console port isn't assigned. Start it in OML Labs and try again.");
                break;
        }
    }

    private void OpenOmlVncSession(OmlLabLink labLink, OmlLabNode node)
    {
        var vnc = new VncControl();
        var name = $"{labLink.LabName}/{node.Name}";
        var item = new TabViewItem { Header = name, Content = vnc, Tag = node };
        vnc.Closed += err => DispatcherQueue.TryEnqueue(() =>
        {
            _connectedTabs.Remove(item);
            if (err is not null) _failedTabs.Add(item);
            item.Header = $"{name} (closed)";
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateStatus();
        });
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;

        _ = ConnectOmlVncAsync(vnc, item, labLink, node, name);
    }

    private async Task ConnectOmlVncAsync(VncControl vnc, TabViewItem item, OmlLabLink labLink, OmlLabNode node, string name)
    {
        var certStore = new OmlHostCertStore();
        var host = new Uri(labLink.OmlHost).Host;
        var wsUri = new Uri($"wss://{host}/api/console/vnc-ws/{node.Id}?token={Uri.EscapeDataString(labLink.Token)}");
        try
        {
            await vnc.ConnectViaWebSocketAsync(wsUri, "", cert =>
                cert is not null && certStore.IsTrusted(host, Convert.ToHexString(cert.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256))));
            _connectedTabs.Add(item);
            if (ReferenceEquals(Tabs.SelectedItem, item)) { UpdateStatus(); vnc.Focus(FocusState.Programmatic); }
        }
        catch (Exception ex)
        {
            _failedTabs.Add(item);
            item.Header = $"{name} (failed)";
            await MessageAsync("Could not connect", $"{ex.Message}\n\nIf {node.Name} isn't running, start it in OML Labs first.");
        }
    }

    private async void OmlNode_Click(object sender, RoutedEventArgs e) => await OmlNodeAsync();

    private async void RegisterLinks_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            ProtocolRegistration.Register(exe);
            await MessageAsync("Links registered", "oml-terminal:// links now open in OML Terminal (current user only). You will still be asked to confirm each connection.");
        }
        catch (Exception ex) { await MessageAsync("Could not register links", ex.Message); }
    }

    private async Task QuickConnectAsync(string text)
    {
        var parsed = QuickConnectParser.Parse(text);
        if (parsed is null)
        {
            await MessageAsync("Quick connect", "Could not understand that. Try: ssh user@host:port, telnet host port, or just a hostname.");
            return;
        }
        await NewSessionAsync(parsed);
    }

    private async Task MessageAsync(string title, string body)
    {
        await ShowDialogAsync(new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        });
    }

    // ---------- tabs ----------

    private void OpenSession(SessionProfile profile)
    {
        profile = _vm.Resolve(profile); // fill in the password manager's credential, if the session links one
        switch (profile.Protocol)
        {
            case ProtocolKind.Rdp: OpenRdpSession(profile); break;
            case ProtocolKind.Vnc: OpenVncSession(profile); break;
            case ProtocolKind.Sftp:
                _vm.NoteRecent(profile);
                Home.SetRecent(_vm.Recent());
                _ = OpenSftpBrowserAsync(profile);
                break;
            default: OpenTerminalSession(profile); break;
        }
        SaveWorkspace();
    }

    private void SaveWorkspace()
    {
        if (_vm.IsLocked || _shuttingDown) return;
        var ids = Tabs.TabItems.OfType<TabViewItem>()
            .SelectMany(item => item.Content switch
            {
                SplitTabView split => split.Panes.Select(p => p.Tab.Profile.Id),
                _ when item.Tag is SessionProfile p => new[] { p.Id },
                _ => Enumerable.Empty<Guid>(),
            }).Distinct().ToList();
        _vm.Settings.WorkspaceSessionIds = ids;
        _vm.SaveSettings();
    }

    /// <summary>Opens RDP in its own tab (embedding the same ActiveX control mstsc.exe is built on), instead of
    /// handing off to a separate mstsc.exe window.</summary>
    private void OpenRdpSession(SessionProfile profile)
    {
        var rdp = new RdpControl();
        var item = new TabViewItem { Header = profile.Name, Content = rdp, Tag = profile };
        rdp.Connected += () => DispatcherQueue.TryEnqueue(() =>
        {
            _connectedTabs.Add(item);
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateStatus();
        });
        rdp.Closed += err => DispatcherQueue.TryEnqueue(() =>
        {
            _connectedTabs.Remove(item);
            if (err is not null) _failedTabs.Add(item);
            item.Header = $"{profile.Name} (closed)";
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateStatus();
        });
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
        _vm.NoteRecent(profile);
        Home.SetRecent(_vm.Recent());

        try { rdp.Connect(profile.Host, profile.Port, profile.Username, profile.Password); }
        catch (Exception ex) { _ = MessageAsync("Could not launch RDP", ex.Message); }
    }

    private void OpenVncSession(SessionProfile profile)
    {
        var vnc = new VncControl();
        var item = new TabViewItem { Header = profile.Name, Content = vnc, Tag = profile };
        vnc.Closed += err => DispatcherQueue.TryEnqueue(() =>
        {
            _connectedTabs.Remove(item);
            if (err is not null) _failedTabs.Add(item);
            item.Header = $"{profile.Name} (closed)";
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateStatus();
        });
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
        _vm.NoteRecent(profile);
        Home.SetRecent(_vm.Recent());

        _ = ConnectVncAsync(vnc, item, profile);
    }

    private async Task ConnectVncAsync(VncControl vnc, TabViewItem item, SessionProfile profile)
    {
        try
        {
            await vnc.ConnectAsync(profile.Host, profile.Port, profile.Password);
            _connectedTabs.Add(item);
            if (ReferenceEquals(Tabs.SelectedItem, item)) { UpdateStatus(); vnc.Focus(FocusState.Programmatic); }
        }
        catch (Exception ex)
        {
            _failedTabs.Add(item);
            item.Header = $"{profile.Name} (failed)";
            await MessageAsync("Could not connect", ex.Message);
        }
    }

    private void OpenTerminalSession(SessionProfile profile)
    {
        if (CreateTerminal(profile) is not { } created) return;
        // Every terminal tab is a SplitTabView, even with one pane: TabView doesn't re-render when the selected tab's
        // Content is swapped, so splitting has to happen inside content that never changes.
        var item = new TabViewItem { Header = profile.Name, IconSource = TabIcon(profile) };
        var split = NewSplitView(item);
        var pane = split.CreatePane(created.Tab, created.Term);
        BindSinkToPane(created.Tab, pane, item, split);
        split.SetFirst(pane);
        item.Content = split;
        item.Tag = split;
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
    }

    /// <summary>Builds a connected-on-first-layout terminal for a profile without deciding where it lives - a new tab
    /// or a new pane in an existing split. Title/state changes go through <see cref="_sinks"/>.</summary>
    private (TerminalTabViewModel Tab, TerminalControl Term)? CreateTerminal(SessionProfile profile)
    {
        profile = _vm.Resolve(profile);
        ITerminalTransport transport;
        try { transport = TransportFactory.Create(profile, _vm.Settings.PlinkPath, _xserver.DisplayVariable); }
        catch (Exception ex) { _ = MessageAsync("Cannot open session", ex.Message); return null; }

        var session = new TerminalSession(transport, new XTermEngine(80, 24, _vm.Settings.ScrollbackLines)
        {
            Highlights = _vm.Settings.HighlightKeywords ? HighlightRuleSet.Default : null,
        });
        if (_vm.Settings.AutoReconnect && profile.Protocol != ProtocolKind.Serial)
            session.ReconnectTransportFactory = () => TransportFactory.Create(profile, _vm.Settings.PlinkPath, _xserver.DisplayVariable);
        var tab = new TerminalTabViewModel(profile, session);
        if (_vm.Settings.AlwaysLogSessions)
        {
            // Best-effort: a bad log directory shouldn't block connecting. The user can still start logging
            // manually afterward, which does surface the error.
            try { tab.Logger = SessionLogger.Start(session, _vm.Settings.LogDirectory, profile.Name); } catch { }
        }
        var term = NewTerminalView(tab);
        term.Ready += async (cols, rows) =>
        {
            // Focus before and after connecting: the pointer-up of a sidebar double-click can hand focus back to the list.
            if (IsShowing(tab) && !_dialogOpen) term.Focus(FocusState.Programmatic);
            if (profile.X11Forwarding) await EnsureXServerAsync(quiet: true);
            await tab.ConnectAsync(cols, rows);
            Sink(tab).State(session.IsConnected, !session.IsConnected);
            if (IsShowing(tab))
            {
                UpdateStatus();
                term.Refresh();
                if (!_dialogOpen) term.Focus(FocusState.Programmatic);
            }
        };
        session.Ended += _ => DispatcherQueue.TryEnqueue(() =>
        {
            Sink(tab).Title($"{profile.Name} (closed)");
            Sink(tab).State(false, true);
            if (IsShowing(tab)) UpdateStatus();
        });
        session.Reconnected += () => DispatcherQueue.TryEnqueue(() =>
        {
            Sink(tab).Title(profile.Name);
            Sink(tab).State(true, false);
            if (IsShowing(tab)) UpdateStatus();
        });

        _sinks[tab] = ((_ => { }), ((_, _) => { }));
        _vm.NoteRecent(profile);
        Home.SetRecent(_vm.Recent());
        return (tab, term);
    }

    private TerminalControl NewTerminalView(TerminalTabViewModel tab)
    {
        var term = new TerminalControl
        {
            TerminalFontFamily = _vm.Settings.FontFamily,
            TerminalFontSize = _vm.Settings.FontSize,
            Session = tab.Session,
        };
        term.GridSizeChanged += (_, _) => { if (IsShowing(tab)) UpdateStatus(); };
        term.GotFocus += (_, _) => _lastTerminal = tab;
        return term;
    }

    private (Action<string> Title, Action<bool, bool> State) Sink(TerminalTabViewModel tab) =>
        _sinks.TryGetValue(tab, out var sink) ? sink : ((_ => { }), ((_, _) => { }));

    private bool IsShowing(TerminalTabViewModel tab) =>
        Tabs.SelectedItem is TabViewItem sel && TerminalsIn(sel).Any(x => ReferenceEquals(x.Tab, tab));

    private static readonly Dictionary<string, Windows.UI.Color> ColorTagColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Red"] = Windows.UI.Color.FromArgb(255, 0xEF, 0x44, 0x44),
        ["Orange"] = Windows.UI.Color.FromArgb(255, 0xF9, 0x73, 0x16),
        ["Yellow"] = Windows.UI.Color.FromArgb(255, 0xFB, 0xBF, 0x24),
        ["Green"] = Windows.UI.Color.FromArgb(255, 0x22, 0xC5, 0x5E),
        ["Blue"] = Windows.UI.Color.FromArgb(255, 0x38, 0xBD, 0xF8),
        ["Purple"] = Windows.UI.Color.FromArgb(255, 0xA7, 0x8B, 0xFA),
    };

    /// <summary>Protocol glyph for the tab, tinted with the session's colour tag so "production" tabs stand out.</summary>
    private static IconSource TabIcon(SessionProfile p)
    {
        var glyph = p.Protocol switch
        {
            ProtocolKind.Local => "\uE756",
            ProtocolKind.Serial => "\uE88E",
            ProtocolKind.Telnet => "\uE8AB",
            _ => "\uE968",
        };
        var icon = new FontIconSource { Glyph = glyph };
        if (ColorTagColors.TryGetValue(p.ColorTag ?? "", out var c)) icon.Foreground = new SolidColorBrush(c);
        return icon;
    }

    // ---------- split view ----------

    private void SplitRight_Click(object sender, RoutedEventArgs e) => ShowSplitPicker(Orientation.Horizontal);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => ShowSplitPicker(Orientation.Vertical);

    /// <summary>Asks what to open in the new pane: the same session again, a local shell, or any saved session.</summary>
    private void ShowSplitPicker(Orientation orientation, TerminalPane? target = null, FrameworkElement? anchor = null)
    {
        var active = ActiveTerminalIn(Tabs.SelectedItem as TabViewItem);
        if (active is null)
        {
            _ = MessageAsync("Split view", "Select a terminal tab first - split view puts another session beside it.");
            return;
        }
        var current = target?.Tab.Profile ?? active.Value.Tab.Profile;
        var menu = new MenuFlyout();
        var dup = new MenuFlyoutItem { Text = $"Duplicate: {current.Name}", Icon = new FontIcon { Glyph = "\uE8C8" } };
        dup.Click += (_, _) => SplitWith(current, orientation, target);
        var shell = new MenuFlyoutItem { Text = "Local shell", Icon = new FontIcon { Glyph = "\uE756" } };
        shell.Click += (_, _) => SplitWith(new SessionProfile { Name = "Local Shell", Protocol = ProtocolKind.Local }, orientation, target);
        menu.Items.Add(dup);
        menu.Items.Add(shell);

        var terminals = _vm.Sessions.Where(s => s.Protocol is not (ProtocolKind.Rdp or ProtocolKind.Vnc or ProtocolKind.Sftp))
            .OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (terminals.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var group in terminals.GroupBy(s => s.Folder))
        {
            IList<MenuFlyoutItemBase> into = menu.Items;
            if (!string.IsNullOrWhiteSpace(group.Key))
            {
                var sub = new MenuFlyoutSubItem { Text = group.Key, Icon = new FontIcon { Glyph = "\uE8B7" } };
                menu.Items.Add(sub);
                into = sub.Items;
            }
            foreach (var profile in group)
            {
                var item = new MenuFlyoutItem { Text = $"{profile.Name}  ·  {profile.ProtocolLabel}" };
                item.Click += (_, _) => SplitWith(profile, orientation, target);
                into.Add(item);
            }
        }
        if (anchor is not null) menu.ShowAt(anchor);
        else menu.ShowAt(Tabs, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = new Windows.Foundation.Point(Tabs.ActualWidth / 2, 40) });
    }

    /// <summary>Opens profile in a new pane next to target (or the tab's active pane).</summary>
    private void SplitWith(SessionProfile profile, Orientation orientation, TerminalPane? target = null)
    {
        if (Tabs.SelectedItem is not TabViewItem { Content: SplitTabView split } item) return;
        target ??= split.ActivePane;
        if (target is null || CreateTerminal(profile) is not { } created) return;
        var pane = split.CreatePane(created.Tab, created.Term);
        BindSinkToPane(created.Tab, pane, item, split);
        split.Split(target, pane, orientation);
        UpdateSplitHeader(item, split);
        UpdateStatus();
        SaveWorkspace();
    }

    private SplitTabView NewSplitView(TabViewItem item)
    {
        var split = new SplitTabView();
        split.ActivePaneChanged += () =>
        {
            if (split.ActivePane is { } p) _lastTerminal = p.Tab;
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateStatus();
        };
        split.SplitRequested += (pane, o) => ShowSplitPicker(o, pane, pane.Term);
        split.PaneCloseRequested += pane => ClosePane(item, split, pane);
        return split;
    }

    private void BindSinkToPane(TerminalTabViewModel tab, TerminalPane pane, TabViewItem item, SplitTabView split) =>
        _sinks[tab] = (title => { pane.SetTitle(title); UpdateSplitHeader(item, split); },
                       (connected, failed) => { pane.SetState(connected, failed); if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateStatus(); });

    /// <summary>One pane: the tab reads like a plain terminal tab. Several: "first +N" with a split (or sync) icon.</summary>
    private static void UpdateSplitHeader(TabViewItem item, SplitTabView split)
    {
        var panes = split.Panes;
        if (panes.Count == 1)
        {
            item.Header = panes[0].Title;
            item.IconSource = TabIcon(panes[0].Tab.Profile);
            return;
        }
        item.Header = panes.Count == 0 ? "Split" : $"{panes[0].Tab.Profile.Name} +{panes.Count - 1}";
        item.IconSource = new FontIconSource { Glyph = split.SyncInput ? "\uE895" : "\uE8A0" };
    }

    /// <summary>Closes one pane's session; closing the last pane closes the tab.</summary>
    private void ClosePane(TabViewItem item, SplitTabView split, TerminalPane pane)
    {
        if (ReferenceEquals(_findTarget, pane.Term)) CloseFindBar();
        int remaining = split.Remove(pane);
        _sinks.Remove(pane.Tab);
        pane.Term.Detach();
        _ = Task.Run(pane.Tab.Close);
        if (remaining == 0) { CloseTabItem(item); UpdateStatus(); return; }
        if (remaining == 1)
        {
            split.SyncInput = false;
            SyncItem.IsChecked = false;
        }
        UpdateSplitHeader(item, split);
        UpdateStatus();
        SaveWorkspace();
    }

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Content: SplitTabView split } item && split.Panes.Count > 1)
        {
            split.SyncInput = SyncItem.IsChecked;
            UpdateSplitHeader(item, split);
            UpdateStatus();
        }
        else
        {
            SyncItem.IsChecked = false;
            _ = MessageAsync("Synchronized typing", "This works inside a split tab: split a terminal first (Ctrl+Shift+D), then turn it on to type into every pane at once. To send one command to many separate tabs, use the Multi-Execution bar (Ctrl+Shift+M).");
        }
    }

    // ---------- tools ----------

    private void OpenTool(string id, Action<UserControl>? configure = null)
    {
        var descriptor = ToolCatalog.Find(id);
        if (descriptor is null) return;
        var existing = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, descriptor));
        if (existing is not null)
        {
            Tabs.SelectedItem = existing;
            if (existing.Content is UserControl v) configure?.Invoke(v);
            return;
        }
        var view = descriptor.Create(_toolContext);
        configure?.Invoke(view);
        var item = new TabViewItem { Header = descriptor.Title, Content = view, Tag = descriptor, IconSource = new FontIconSource { Glyph = descriptor.Glyph } };
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
    }

    private void ToolsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolDescriptor d) OpenTool(d.Id);
    }

    private void SidebarBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool tools = sender.SelectedItem == sender.Items[1];
        ToolsList.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
        SessionsPanel.Visibility = tools ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildToolsMenu()
    {
        int index = 0;
        foreach (var tool in ToolCatalog.All)
        {
            var t = tool;
            var item = new MenuFlyoutItem { Text = t.Title, Icon = new FontIcon { Glyph = t.Glyph } };
            item.Click += (_, _) => OpenTool(t.Id);
            ToolsMenu.Items.Insert(index++, item);
        }
    }

    private void BuildToolbar()
    {
        Button Add(string glyph, string label, string tip, Action action, string? accentKey = null)
        {
            var icon = new FontIcon { Glyph = glyph, FontSize = 18 };
            if (accentKey is not null) icon.Foreground = (Brush)Application.Current.Resources[accentKey];
            var panel = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(icon);
            panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["ToolbarLabelStyle"] });
            var button = new Button { Content = panel, Style = (Style)Application.Current.Resources["ToolbarButtonStyle"] };
            ToolTipService.SetToolTip(button, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, tip);
            button.Click += (_, _) => action();
            Toolbar.Children.Add(button);
            return button;
        }
        void Separator() => Toolbar.Children.Add(new Border { Style = (Style)Application.Current.Resources["ToolbarSeparatorStyle"] });

        Add("\uE710", "Session", "New session (Ctrl+N)", () => _ = NewSessionAsync(), "OmlOrangeBrush");
        Add("\uE756", "Shell", "Local shell: PowerShell, cmd, WSL, Git Bash, Cygwin", OpenDefaultLocalShell);
        Separator();
        Button? splitButton = null;
        splitButton = Add("\uE8A0", "Split", "Split the active terminal right (Ctrl+Shift+D); Ctrl+Shift+E splits down",
            () => ShowSplitPicker(Orientation.Horizontal, anchor: splitButton), "OmlSkyBrush");
        Add("\uE71D", "MultiExec", "Send one command to many tabs (Ctrl+Shift+M)", () => MultiExec_Click(this, new RoutedEventArgs()), "OmlSkyBrush");
        Add("\uE8AB", "Tunnels", "SSH port forwards on the active session (Ctrl+Shift+T)", () => Tunnels_Click(this, new RoutedEventArgs()), "OmlSkyBrush");
        Add("\uE8B7", "SFTP", "Browse files on the active SSH session (Ctrl+Shift+F)", () => SftpBrowser_Click(this, new RoutedEventArgs()), "OmlSkyBrush");
        Add("\uE71A", "Break", "Interrupt a running ping/traceroute on the active session (Ctrl+^, i.e. Ctrl+Shift+6) - Cisco IOS ignores Ctrl+C mid-ping", () => SendBreakToActiveSession(), "OmlRoseBrush");
        Separator();
        foreach (var tool in ToolCatalog.All.Where(t => t.Category == "Network"))
        {
            var t = tool;
            Add(t.Glyph, ShortLabel(t), t.Subtitle, () => OpenTool(t.Id), "OmlMintBrush");
        }
        Separator();
        foreach (var tool in ToolCatalog.All.Where(t => t.Category == "Security"))
        {
            var t = tool;
            Add(t.Glyph, ShortLabel(t), t.Subtitle, () => OpenTool(t.Id), "OmlVioletBrush");
        }
        Separator();
        foreach (var tool in ToolCatalog.All.Where(t => t.Category is not ("Network" or "Security")))
        {
            var t = tool;
            Add(t.Glyph, ShortLabel(t), t.Subtitle, () => OpenTool(t.Id), "OmlAmberBrush");
        }
        Separator();
        Add("\uE7F4", "X server", "Start/stop the local X server for X11 forwarding", () => XServer_Click(this, new RoutedEventArgs()));
        Add("\uE713", "Settings", "Settings", () => Settings_Click(this, new RoutedEventArgs()));
    }

    private static string ShortLabel(ToolDescriptor t) => t.Id switch
    {
        "portscan" => "Ports",
        "localports" => "Netstat",
        "ping" => "Ping",
        "dns" => "DNS",
        "subnet" => "Subnet",
        "capture" => "Capture",
        "fwobjects" => "FW Objects",
        "fwpolicy" => "FW Policy",
        "vault" => "Passwords",
        "backup" => "Backup",
        "cliguide" => "CLI Guide",
        "netservices" => "Servers",
        "topology" => "Topology",
        "tables" => "Tables",
        "changeguard" => "Change Guard",
        "search" => "Search",
        _ => t.Title,
    };

    private void OpenDefaultLocalShell() =>
        OpenSession(new SessionProfile { Name = "Local Shell", Protocol = ProtocolKind.Local });

    /// <summary>Types text into the active terminal - or the last one used, when a tool tab is in front - one line at a
    /// time with a short gap, so device CLIs that read line-by-line (FortiOS, PAN-OS) don't drop pasted config.</summary>
    private bool SendToActiveSession(string text)
    {
        var target = SelectedTerminalTab() ?? _lastTerminal;
        if (target is null || !target.Session.IsConnected || !AllTerminalTabs().Contains(target)) return false;
        var lines = Core.TextLines.Split(text.TrimEnd('\r', '\n'));
        _ = Task.Run(async () =>
        {
            foreach (var line in lines)
            {
                target.Session.Send(System.Text.Encoding.UTF8.GetBytes(line + "\r"));
                await Task.Delay(40);
            }
        });
        return true;
    }

    /// <summary>Sends Cisco IOS's "abort a running ping/traceroute" escape (Ctrl+^, byte 0x1E) to the active
    /// session - also reachable via Ctrl+Shift+6 typed directly into the terminal. Cisco IOS ignores Ctrl+C
    /// while a ping/traceroute is running, which is why people get stuck unable to exit.</summary>
    private bool SendBreakToActiveSession()
    {
        var target = SelectedTerminalTab() ?? _lastTerminal;
        if (target is null || !target.Session.IsConnected || !AllTerminalTabs().Contains(target)) return false;
        target.Session.Send([0x1E]);
        return true;
    }

    /// <summary>Types one command into the active terminal without Enter, so it can be reviewed and run manually.</summary>
    private bool InsertIntoActiveSession(string text)
    {
        var target = SelectedTerminalTab() ?? _lastTerminal;
        if (target is null || !target.Session.IsConnected || !AllTerminalTabs().Contains(target)) return false;
        target.Session.Send(System.Text.Encoding.UTF8.GetBytes(text.TrimEnd('\r', '\n')));
        DispatcherQueue.TryEnqueue(() => ActiveTerminalIn(Tabs.SelectedItem as TabViewItem)?.Term.Focus(FocusState.Programmatic));
        return true;
    }

    // ---------- X server ----------

    private async void XServer_Click(object sender, RoutedEventArgs e)
    {
        if (_xserver.OwnsProcess) { _xserver.Stop(); await RefreshXServerIndicatorAsync(); return; }
        await EnsureXServerAsync(quiet: false);
    }

    private async Task EnsureXServerAsync(bool quiet)
    {
        if (XServerManager.DetectInstalled().Count == 0 && string.IsNullOrWhiteSpace(_vm.Settings.XServerPath) && !_xserver.IsListening())
        {
            if (!quiet) await OfferXServerInstallAsync();
            await RefreshXServerIndicatorAsync();
            return;
        }
        try
        {
            XServerText.Text = "X starting...";
            var status = await _xserver.EnsureRunningAsync();
            if (!quiet) await MessageAsync("X server", $"{status}.\n\nSSH sessions using the OpenSSH engine with X11 forwarding will display here (DISPLAY={_xserver.DisplayVariable}).");
        }
        catch (Exception ex)
        {
            if (!quiet) await MessageAsync("X server", ex.Message);
        }
        await RefreshXServerIndicatorAsync();
    }

    /// <summary>MobaXterm-style: the X server comes up with the app. The first time there's no X server installed,
    /// offer to install one (once - "Not now" is remembered).</summary>
    private async Task StartXServerOnLaunchAsync()
    {
        bool installed = XServerManager.DetectInstalled().Count > 0 || !string.IsNullOrWhiteSpace(_vm.Settings.XServerPath) || _xserver.IsListening();
        if (!installed && !_vm.Settings.XServerInstallDeclined) await OfferXServerInstallAsync();
        else if (installed) await EnsureXServerAsync(quiet: true);
        else await RefreshXServerIndicatorAsync();
    }

    private async Task OfferXServerInstallAsync()
    {
        if (!XServerManager.CanInstall)
        {
            await MessageAsync("X server", "No X server is installed. Install VcXsrv from https://github.com/marchaesen/vcxsrv/releases, then click the X server button.");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = "Install an X server?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "OML Terminal starts an X server with the app so X11-forwarded programs (Wireshark, xterm, virt-manager...) open on this PC - but none is installed.\n\n" +
                       "Install VcXsrv (free, GPL) now with winget? Windows will ask for administrator approval.",
            },
            PrimaryButtonText = "Install VcXsrv",
            SecondaryButtonText = "Not now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var choice = await ShowDialogAsync(dialog);
        if (choice == ContentDialogResult.Secondary)
        {
            _vm.Settings.XServerInstallDeclined = true;
            _vm.SaveSettings();
            return;
        }
        if (choice != ContentDialogResult.Primary) return;

        XServerText.Text = "Installing X...";
        try
        {
            var (ok, output) = await XServerManager.InstallVcXsrvAsync();
            if (!ok)
            {
                await MessageAsync("X server", "VcXsrv wasn't installed (cancelled or failed).\n\n" + output.Trim()[^Math.Min(600, output.Trim().Length)..]);
                await RefreshXServerIndicatorAsync();
                return;
            }
            _vm.Settings.XServerInstallDeclined = false;
            _vm.SaveSettings();
            await EnsureXServerAsync(quiet: false);
        }
        catch (Exception ex)
        {
            await MessageAsync("X server", ex.Message);
            await RefreshXServerIndicatorAsync();
        }
    }

    private async Task RefreshXServerIndicatorAsync()
    {
        bool listening = await Task.Run(_xserver.IsListening);
        XServerDot.Fill = (Brush)Application.Current.Resources[listening ? "StatusConnectedBrush" : "StatusIdleBrush"];
        XServerText.Text = listening ? $"X :{_xserver.Display}" : "X off";
        XServerMenuItem.Text = _xserver.OwnsProcess ? "Stop X Server" : listening ? "X Server running (external)" : "Start X Server";
        ToolTipService.SetToolTip(XServerButton, listening
            ? $"X server listening on {_xserver.DisplayVariable}{(_xserver.OwnsProcess ? " - click to stop" : " (started outside OML Terminal)")}"
            : "X server off - click to start VcXsrv / Xming / Cygwin/X");
    }

    private void CloseTabItem(TabViewItem item)
    {
        if (ReferenceEquals(_findTarget, item.Content)) CloseFindBar();
        Tabs.TabItems.Remove(item);
        SaveWorkspace();
        _connectedTabs.Remove(item);
        _failedTabs.Remove(item);
        var content = item.Content;
        item.Content = null;
        switch (content)
        {
            case SplitTabView split:
                foreach (var pane in split.Panes)
                {
                    pane.Term.Detach();
                    _sinks.Remove(pane.Tab);
                    _ = Task.Run(pane.Tab.Close); // disconnecting can block on the network; keep the UI thread free
                }
                break;
            case IToolView tool:
                tool.Shutdown();
                break;
            case VncControl vnc:
                vnc.Detach();
                break;
            case RdpControl rdp:
                rdp.Detach();
                break;
            case OmlLabTabView labView:
                labView.Detach();
                break;
        }
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is TabViewItem item) CloseTabItem(item);
        UpdateStatus();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FindBar.Visibility == Visibility.Visible) CloseFindBar();
        UpdateStatus();
        UpdateLogMenuState();
        var selected = Tabs.SelectedItem as TabViewItem;
        SyncItem.IsChecked = selected?.Content is SplitTabView { SyncInput: true };
        if (ActiveTerminalIn(selected) is { } active)
        {
            _lastTerminal = active.Tab;
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var (_, term) in TerminalsIn(selected!)) term.Refresh();
                active.Term.Focus(FocusState.Programmatic);
            });
        }
        else if (selected?.Content is VncControl vnc) DispatcherQueue.TryEnqueue(() => vnc.Focus(FocusState.Programmatic));
    }

    private static Brush ConnectedBrush => (Brush)Application.Current.Resources["StatusConnectedBrush"];
    private static Brush ConnectingBrush => (Brush)Application.Current.Resources["StatusConnectingBrush"];
    private static Brush ErrorBrush => (Brush)Application.Current.Resources["StatusErrorBrush"];
    private static Brush IdleBrush => (Brush)Application.Current.Resources["StatusIdleBrush"];

    /// <summary>RDP/VNC/OML-VNC tabs don't have a TerminalSession.IsConnected to read - real state comes from
    /// the _connectedTabs/_failedTabs tracking wired up where each of those tabs is created.</summary>
    private Brush ConnectionBrushFor(TabViewItem item) =>
        _connectedTabs.Contains(item) ? ConnectedBrush : _failedTabs.Contains(item) ? ErrorBrush : ConnectingBrush;

    private void UpdateStatus()
    {
        int terminals = AllTerminalTabs().Count(), connected = AllTerminalTabs().Count(t => t.Session.IsConnected);
        StatusTabs.Text = terminals == 0 ? "" : $"{connected}/{terminals} connected";
        var selectedItem = Tabs.SelectedItem as TabViewItem;
        if (ActiveTerminalIn(selectedItem) is { } active)
        {
            var (tab, term) = active;
            var split = (SplitTabView)selectedItem!.Content;
            int panes = split.Panes.Count;
            StatusProtocol.Text = tab.ProtocolText + (panes < 2 ? "" : split.SyncInput ? $"  ·  SPLIT ×{panes}  ·  SYNC" : $"  ·  SPLIT ×{panes}");
            StatusHost.Text = (_recorder is null ? "" : "● REC   ") + tab.HostText;
            StatusSize.Text = $"{term.Cols}x{term.Rows}";
            StatusDot.Fill = tab.Session.IsConnected ? ConnectedBrush : ErrorBrush;
            return;
        }
        switch (Tabs.SelectedItem)
        {
            case TabViewItem { Tag: ToolDescriptor tool }:
                StatusProtocol.Text = "TOOL";
                StatusHost.Text = tool.Title;
                StatusSize.Text = "";
                StatusDot.Fill = ConnectedBrush;
                break;
            case TabViewItem { Tag: SessionProfile p, Content: VncControl } vncItem:
                StatusProtocol.Text = "VNC";
                StatusHost.Text = $"{p.Host}:{p.Port}";
                StatusSize.Text = "";
                StatusDot.Fill = ConnectionBrushFor(vncItem);
                break;
            case TabViewItem { Tag: SessionProfile rp, Content: RdpControl } rdpItem:
                StatusProtocol.Text = "RDP";
                StatusHost.Text = $"{rp.Host}:{rp.Port}";
                StatusSize.Text = "";
                StatusDot.Fill = ConnectionBrushFor(rdpItem);
                break;
            case TabViewItem { Tag: OmlLabNode node, Content: VncControl } omlVncItem:
                StatusProtocol.Text = "VNC";
                StatusHost.Text = $"OML Labs · {node.Name}";
                StatusSize.Text = "";
                StatusDot.Fill = ConnectionBrushFor(omlVncItem);
                break;
            case TabViewItem { Content: OmlLabTabView labView }:
                StatusProtocol.Text = "OML LAB";
                StatusHost.Text = $"{labView.Link.LabName} · {new Uri(labView.Link.OmlHost).Host}";
                StatusSize.Text = "";
                StatusDot.Fill = ConnectedBrush;
                break;
            default:
                StatusDot.Fill = IdleBrush;
                StatusProtocol.Text = "Ready";
                StatusHost.Text = "";
                StatusSize.Text = "";
                break;
        }
    }

    // ---------- menu ----------

    private async void NewSession_Click(object sender, RoutedEventArgs e) => await NewSessionAsync();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var dialog = new SettingsDialog(RootGrid.XamlRoot, _vm.Settings);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            try
            {
                if (dialog.RemoveMasterPassword) _vm.RemoveMasterPassword();
                else if (dialog.NewMasterPassword is { } pw) _vm.SetMasterPassword(pw);
                _vm.SaveSettings();
                ApplyFont();
                _xserver.Display = _vm.Settings.XDisplay;
                _xserver.ConfiguredPath = _vm.Settings.XServerPath;
                await RefreshXServerIndicatorAsync();
            }
            catch (Exception ex) { await MessageAsync("Could not save settings", ex.Message); }
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+W in a split closes the focused pane (Windows Terminal style); the last pane closes the tab.
        if (Tabs.SelectedItem is TabViewItem { Content: SplitTabView split } splitItem && split.ActivePane is { } pane)
            ClosePane(splitItem, split, pane);
        else if (Tabs.SelectedItem is TabViewItem item && !ReferenceEquals(item, HomeTab)) CloseTabItem(item);
        UpdateStatus();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(+1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(-1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.FontSize = MainViewModel.DefaultFont;
        ApplyFont();
    }

    private void Zoom(double delta)
    {
        _vm.Settings.FontSize = Math.Clamp(_vm.Settings.FontSize + delta, MainViewModel.MinFont, MainViewModel.MaxFont);
        ApplyFont();
    }

    private void ApplyFont()
    {
        _vm.SaveSettings();
        foreach (var (_, t) in Tabs.TabItems.OfType<TabViewItem>().SelectMany(TerminalsIn))
        {
            t.TerminalFontFamily = _vm.Settings.FontFamily;
            t.TerminalFontSize = _vm.Settings.FontSize;
        }
        UpdateStatus();
    }

    private async void About_Click(object sender, RoutedEventArgs e) =>
        await MessageAsync("About OML Terminal",
            "OML Terminal\n\nSSH (built-in, OpenSSH with X11, or PuTTY), Telnet, Serial, RDP, VNC, SFTP and local shells (PowerShell, WSL, Cygwin, Git Bash) " +
            "with split view, multi-exec, tunnels, macros - plus a network & security toolkit: port query, netstat, ping/trace/sweep, DNS, subnet calculator, " +
            "packet capture, firewall object builder and config backup.\n\nFree, no telemetry, no lock-in.");
}
