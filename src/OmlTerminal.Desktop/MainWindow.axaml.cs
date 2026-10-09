using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmlTerminal.App.ViewModels;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Shells;
using OmlTerminal.Core.Terminal;
using OmlTerminal.Core.Transports;
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
        UpdateStartPanel();
        Opened += async (_, _) =>
        {
            if (_vm.IsLocked && !await UnlockAsync()) { Close(); return; }
            RefreshTree();
            Core.Snmp.TrafficGrapher.Shared.Start(); // MRTG polling runs while the app is open, tool tab or not
            StartBackupScheduler();
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
        item.ContextMenu = new ContextMenu { ItemsSource = new Control[] { connect, edit, duplicate, new Separator(), delete } };
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

    private async void ImportPutty_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsLocked) return;
        var found = PuttySessionImporter.FindSessions();
        var fresh = found.Where(f => !_vm.Sessions.Any(s => s.Name.Equals(f.Profile.Name, StringComparison.OrdinalIgnoreCase)
                                                          && s.Host.Equals(f.Profile.Host, StringComparison.OrdinalIgnoreCase))).ToList();
        if (fresh.Count == 0)
        {
            await Dialogs.MessageAsync(this, "Import PuTTY sessions", found.Count == 0
                ? "No PuTTY saved sessions were found" + (OperatingSystem.IsWindows() ? "." : " in ~/.putty/sessions.")
                : "All of your PuTTY sessions are already in OML Terminal.");
            return;
        }
        int ppk = fresh.Count(f => f.KeyNeedsConversion);
        var msg = $"Import {fresh.Count} PuTTY session(s) into the \"Imported from PuTTY\" folder?";
        if (ppk > 0) msg += $"\n\n{ppk} use a .ppk key, which needs converting to OpenSSH format (puttygen key.ppk -O private-openssh -o key) - they're imported with password login meanwhile.";
        if (!await Dialogs.ConfirmAsync(this, "Import PuTTY sessions", msg, "Import")) return;
        foreach (var f in fresh) TrySave(() => _vm.Add(f.Profile));
        RefreshTree();
        Status($"Imported {fresh.Count} PuTTY session(s).");
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

    private void Connect(SessionProfile saved, bool remember = true)
    {
        var p = _vm.Resolve(saved);
        if (p.Protocol is not (ProtocolKind.Ssh or ProtocolKind.Telnet or ProtocolKind.Serial or ProtocolKind.Local))
        {
            _ = Dialogs.MessageAsync(this, "Not available yet", $"{p.Protocol} sessions aren't available in the Linux/macOS app yet - they're coming in a later update.");
            return;
        }
        ITerminalTransport transport;
        try { transport = TransportFactory.Create(p); }
        catch (Exception ex) { _ = Dialogs.MessageAsync(this, "Can't open session", ex.Message); return; }

        var engine = new XTermEngine(scrollback: Math.Clamp(_vm.Settings.ScrollbackLines, 500, 200_000));
        if (_vm.Settings.HighlightKeywords) engine.Highlights = HighlightRuleSet.Default;
        var session = new TerminalSession(transport, engine);
        if (_vm.Settings.AutoReconnect && p.Protocol is ProtocolKind.Ssh or ProtocolKind.Telnet)
            session.ReconnectTransportFactory = () => TransportFactory.Create(_vm.Resolve(saved));
        var tabVm = new TerminalTabViewModel(p, session);

        var view = new TerminalView { TerminalFontSize = _fontSize };
        view.TerminalFontFamily = _vm.Settings.FontFamily;
        view.Session = session;
        view.Ready += (cols, rows) => _ = tabVm.ConnectAsync(cols, rows);
        view.GridSizeChanged += (cols, rows) => { if (CurrentTab()?.View == view) StatusSize.Text = $"{cols} × {rows}"; };

        var title = new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = (IBrush)this.FindResource("OmlAccent")!, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "×", FontSize = 10, Padding = new Thickness(5, 1), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, "Close tab (Ctrl+W)");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, title, close } };
        ToolTip.SetTip(header, $"{tabVm.ProtocolText} · {tabVm.HostText}");
        var tab = new TabItem { Header = header, Content = view, FontSize = 13 };
        close.Click += (_, _) => CloseTab(tab);
        header.PointerPressed += (_, e) => { if (e.GetCurrentPoint(header).Properties.IsMiddleButtonPressed) CloseTab(tab); };

        session.Ended += _ => Dispatcher.UIThread.Post(() =>
        {
            dot.Fill = Brushes.Gray;
            if (CurrentTab()?.Vm == tabVm) Status($"{p.Name}: disconnected");
        });
        session.Reconnected += () => Dispatcher.UIThread.Post(() => dot.Fill = (IBrush)this.FindResource("OmlAccent")!);

        _tabs[tab] = (tabVm, view);
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateStartPanel();
        if (remember && !_vm.IsLocked && _vm.Sessions.Contains(saved)) { _vm.NoteRecent(saved); }
        Dispatcher.UIThread.Post(() => view.Focus(), DispatcherPriority.Background);
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
        if (_lastTerminal?.View == t.View) _lastTerminal = null;
        t.View.Detach();
        try { t.Vm.Close(); } catch { }
        Tabs.Items.Remove(tab);
        UpdateStartPanel();
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

    // ---------- edit / view ----------

    private void Copy_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab()?.View.CopySelectionAsync();
    private void Paste_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab()?.View.PasteAsync();

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetFont(_fontSize + 1);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetFont(_fontSize - 1);
    private void ZoomReset_Click(object? sender, RoutedEventArgs e) => SetFont(MainViewModel.DefaultFont);

    private void SetFont(double size)
    {
        _fontSize = Math.Clamp(size, MainViewModel.MinFont, MainViewModel.MaxFont);
        foreach (var (_, view) in _tabs.Values) view.TerminalFontSize = _fontSize;
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
            (true, Key.Q) => Close,
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
