using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Shells;
using OmlTerminal.Core.Transports;

namespace OmlTerminal.App.Views;

/// <summary>
/// A fresh instance is created per invocation, and the result is built only inside a button-click handler,
/// so a cancelled/closed dialog can never leave a stale profile behind.
/// </summary>
public sealed partial class NewSessionDialog : ContentDialog
{
    /// <summary>Tile kinds: every ProtocolKind, plus WSL - which is stored as a Local profile running wsl.exe.</summary>
    private enum TileKind { Ssh, Telnet, Serial, Rdp, Vnc, Sftp, Shell, Wsl }

    private static readonly (TileKind Kind, string Label, string Glyph)[] TileDefs =
    [
        (TileKind.Ssh, "SSH", "\uE968"),
        (TileKind.Telnet, "Telnet", "\uE8AB"),
        (TileKind.Serial, "Serial", "\uE88E"),
        (TileKind.Rdp, "RDP", "\uE7F4"),
        (TileKind.Vnc, "VNC", "\uE7F8"),
        (TileKind.Sftp, "SFTP", "\uE8B7"),
        (TileKind.Shell, "Shell", "\uE756"),
        (TileKind.Wsl, "WSL", "\uE7C4"),
    ];

    private static readonly int[] BaudRates = [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    private readonly Guid _id;
    private readonly List<string> _folders;
    private readonly Dictionary<TileKind, ToggleButton> _tiles = new();
    private readonly IReadOnlyList<ShellInfo> _shells = ShellCatalog.Detect();
    private TileKind _kind = TileKind.Ssh;
    private bool _nameTouched;
    private bool _suppressNameTracking;

    /// <summary>Non-null only after a successful Save / Save &amp; Connect click.</summary>
    public SessionProfile? Result { get; private set; }

    public NewSessionDialog(XamlRoot xamlRoot, SessionProfile? existing = null, IEnumerable<string>? existingFolders = null, string? initialFolder = null,
        IReadOnlyList<Credential>? credentials = null)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        PrimaryButtonClick += OnCommit;
        SecondaryButtonClick += OnCommit;

        _folders = (existingFolders ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        _id = existing?.Id ?? Guid.NewGuid();
        Title = existing is null ? "New session" : $"Edit session · {existing.Name}";

        foreach (var (kind, label, glyph) in TileDefs) TypeTiles.Children.Add(BuildTile(kind, label, glyph));
        foreach (var b in BaudRates) BaudBox.Items.Add(b.ToString());
        foreach (var p in SerialTransport.AvailablePorts()) SerialPortBox.Items.Add(p);
        foreach (var c in SessionProfile.ColorTags) ColorBox.Items.Add(c.Length == 0 ? "None" : c);
        foreach (var sh in _shells.Where(s => s.Kind != ShellKind.Wsl)) ShellPicker.Items.Add(sh);
        ShellPicker.Items.Add("Custom executable...");

        _suppressNameTracking = true;
        NameBox.Text = existing?.Name ?? "";
        _suppressNameTracking = false;
        _nameTouched = existing is not null;
        NameBox.TextChanged += (_, _) => { if (!_suppressNameTracking) _nameTouched = true; };

        FolderBox.Text = existing?.Folder ?? initialFolder ?? "";
        HostBox.Text = existing?.Host ?? "";
        UserBox.Text = existing?.Username ?? "";
        PasswordBox.Password = existing?.Password ?? "";
        EnablePasswordBox.Password = existing?.EnablePassword ?? "";
        CredentialBox.Items.Add(new ComboBoxItem { Content = "None - use the username / password on this session", Tag = null });
        foreach (var c in credentials ?? []) CredentialBox.Items.Add(new ComboBoxItem { Content = c.ToString(), Tag = c });
        CredentialBox.SelectedItem = CredentialBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Credential c && c.Id == existing?.CredentialId)
                                     ?? CredentialBox.Items[0];
        SelectByTag(EngineBox, (existing?.Engine ?? TransportEngine.BuiltIn).ToString());
        SelectByTag(AuthMethodBox, (existing?.AuthMethod ?? SshAuthMethod.Password).ToString());
        KeyPathBox.Text = existing?.PrivateKeyPath ?? "";
        KeyPassphraseBox.Password = existing?.PrivateKeyPassphrase ?? "";
        X11Box.IsChecked = existing?.X11Forwarding ?? false;
        StartupBox.Text = existing?.StartupCommands ?? "";
        NotesBox.Text = existing?.Notes ?? "";
        TagsBox.Text = existing?.Tags ?? "";
        ColorBox.SelectedIndex = Math.Max(0, SessionProfile.ColorTags.ToList().IndexOf(existing?.ColorTag ?? ""));
        PortBox.Value = existing?.Port ?? SessionProfile.DefaultPortFor(ProtocolKind.Ssh);

        SerialPortBox.Text = existing?.SerialPortName ?? "";
        if (SerialPortBox.Text.Length == 0 && SerialPortBox.Items.Count > 0) SerialPortBox.SelectedIndex = 0;
        BaudBox.Text = (existing?.BaudRate ?? 9600).ToString();
        ShellPathBox.Text = existing?.LocalShellPath ?? "";
        ShellArgsBox.Text = existing?.LocalShellArgs ?? "";

        UseJumpBox.IsChecked = existing?.UseJumpHost ?? false;
        JumpHostBox.Text = existing?.JumpHost ?? "";
        JumpPortBox.Value = existing?.JumpPort ?? 22;
        JumpUserBox.Text = existing?.JumpUsername ?? "";
        JumpPasswordBox.Password = existing?.JumpPassword ?? "";

        SelectTile(existing is null ? TileKind.Ssh : KindOf(existing), resetPort: false);
        if (existing is { Protocol: ProtocolKind.Local }) MatchShellPicker();
        if (existing is not null) SectionBar.SelectedItem = BookmarkTab;
    }

    private static TileKind KindOf(SessionProfile p) => p.Protocol switch
    {
        ProtocolKind.Telnet => TileKind.Telnet,
        ProtocolKind.Serial => TileKind.Serial,
        ProtocolKind.Rdp => TileKind.Rdp,
        ProtocolKind.Vnc => TileKind.Vnc,
        ProtocolKind.Sftp => TileKind.Sftp,
        ProtocolKind.Local when Path.GetFileName(p.LocalShellPath).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) => TileKind.Wsl,
        ProtocolKind.Local => TileKind.Shell,
        _ => TileKind.Ssh,
    };

    private static ProtocolKind ProtocolOf(TileKind k) => k switch
    {
        TileKind.Telnet => ProtocolKind.Telnet,
        TileKind.Serial => ProtocolKind.Serial,
        TileKind.Rdp => ProtocolKind.Rdp,
        TileKind.Vnc => ProtocolKind.Vnc,
        TileKind.Sftp => ProtocolKind.Sftp,
        TileKind.Shell or TileKind.Wsl => ProtocolKind.Local,
        _ => ProtocolKind.Ssh,
    };

    private ToggleButton BuildTile(TileKind kind, string label, string glyph)
    {
        var panel = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22 });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        var tile = new ToggleButton
        {
            Content = panel,
            Width = 86,
            Height = 70,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, label);
        tile.Click += (_, _) => SelectTile(kind, resetPort: true);
        _tiles[kind] = tile;
        return tile;
    }

    private void SelectTile(TileKind kind, bool resetPort)
    {
        _kind = kind;
        foreach (var (k, t) in _tiles) t.IsChecked = k == kind;
        var protocol = ProtocolOf(kind);

        // Only auto-fill when the port is still one of the defaults, so a custom port is not clobbered.
        if (resetPort && (double.IsNaN(PortBox.Value) || SessionProfile.DefaultPorts.Values.Contains((int)PortBox.Value)))
            PortBox.Value = SessionProfile.DefaultPortFor(protocol);

        if (kind == TileKind.Wsl && resetPort)
        {
            var distro = ShellCatalog.WslDistros().FirstOrDefault();
            ShellPathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            ShellArgsBox.Text = distro is null ? "--cd ~" : $"-d {distro} --cd ~";
            ShellHint.Text = distro is null
                ? "No WSL distributions found. Install one with: wsl --install -d Ubuntu"
                : $"Installed distributions: {string.Join(", ", ShellCatalog.WslDistros())} - change -d to pick another.";
            SuggestName(distro is null ? "WSL" : $"WSL {distro}");
        }
        else if (kind == TileKind.Shell && resetPort)
        {
            if (Path.GetFileName(ShellPathBox.Text).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase)) { ShellPathBox.Text = ""; ShellArgsBox.Text = ""; }
            if (ShellPicker.Items.Count > 1 && ShellPathBox.Text.Length == 0) ShellPicker.SelectedIndex = 0;
        }
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var protocol = ProtocolOf(_kind);
        bool serial = protocol == ProtocolKind.Serial;
        bool local = protocol == ProtocolKind.Local;
        bool ssh = protocol == ProtocolKind.Ssh;
        bool sshBased = protocol is ProtocolKind.Ssh or ProtocolKind.Sftp;

        BasicHeader.Text = $"BASIC {TileDefs.First(t => t.Kind == _kind).Label.ToUpperInvariant()} SETTINGS";
        NetworkFieldsPanel.Visibility = serial || local ? Visibility.Collapsed : Visibility.Visible;
        SerialFieldsPanel.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        LocalFieldsPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        ShellPicker.Visibility = _kind == TileKind.Shell ? Visibility.Visible : Visibility.Collapsed;
        UserBox.IsEnabled = protocol is not ProtocolKind.Vnc && SelectedCredential is null;
        CredentialRow.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        PortBox.IsEnabled = true;

        SshAdvanced.Visibility = sshBased ? Visibility.Visible : Visibility.Collapsed;
        EngineBox.IsEnabled = ssh;
        X11Box.IsEnabled = ssh;
        PasswordBox.Visibility = serial || local ? Visibility.Collapsed : Visibility.Visible;
        StartupBox.Visibility = protocol is ProtocolKind.Rdp or ProtocolKind.Vnc or ProtocolKind.Sftp ? Visibility.Collapsed : Visibility.Visible;
        NetworkTab.IsEnabled = protocol is ProtocolKind.Ssh or ProtocolKind.Telnet or ProtocolKind.Sftp;
        if (!NetworkTab.IsEnabled && SectionBar.SelectedItem == NetworkTab) SectionBar.SelectedItem = AdvancedTab;
        UpdateEngineState();
    }

    private TransportEngine Engine => Enum.Parse<TransportEngine>(TagOf(EngineBox));

    /// <summary>Keeps engine-specific options consistent instead of letting an invalid combo be typed in:
    /// X11 needs OpenSSH, key auth doesn't work with plink, jump hosts need the built-in engine.</summary>
    private Credential? SelectedCredential => (CredentialBox.SelectedItem as ComboBoxItem)?.Tag as Credential;

    /// <summary>A linked credential supplies the login, so the session's own username/password boxes step aside.</summary>
    private void CredentialBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UserBox is null) return;
        var cred = SelectedCredential;
        UserBox.IsEnabled = cred is null && ProtocolOf(_kind) is not ProtocolKind.Vnc;
        UserBox.PlaceholderText = cred is null ? "admin" : $"{cred.Username} (from {cred.Name})";
        PasswordBox.IsEnabled = cred is null;
        PasswordBox.Header = cred is null ? "Password (saved encrypted; leave blank to be prompted)" : $"Password - from Password Manager '{cred.Name}'";
        EnablePasswordBox.PlaceholderText = cred?.EnablePassword.Length > 0 ? $"from '{cred.Name}'" : "";
    }

    private void UpdateEngineState()
    {
        if (EngineBox.SelectedItem is null || AuthMethodBox.SelectedItem is null) return;
        var protocol = ProtocolOf(_kind);
        var engine = protocol == ProtocolKind.Ssh ? Engine : TransportEngine.BuiltIn;

        bool plink = engine == TransportEngine.Plink;
        AuthMethodBox.IsEnabled = !plink;
        if (plink) SelectByTag(AuthMethodBox, nameof(SshAuthMethod.Password));
        bool keyAuth = protocol is ProtocolKind.Ssh or ProtocolKind.Sftp && TagOf(AuthMethodBox) == nameof(SshAuthMethod.PrivateKey);
        KeyFieldsPanel.Visibility = keyAuth ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Visibility = keyAuth || protocol is ProtocolKind.Serial or ProtocolKind.Local ? Visibility.Collapsed : Visibility.Visible;

        bool jumpOk = protocol is ProtocolKind.Telnet or ProtocolKind.Sftp || protocol == ProtocolKind.Ssh && engine == TransportEngine.BuiltIn;
        UseJumpBox.IsEnabled = jumpOk;
        if (!jumpOk) UseJumpBox.IsChecked = false;

        EngineHint.Text = engine switch
        {
            TransportEngine.OpenSsh => "Uses Windows' ssh.exe: X11, agent and ~/.ssh/config. Saved password is typed at the prompt.",
            TransportEngine.Plink => "Needs PuTTY's plink.exe. For old gear with legacy key exchange / ciphers.",
            _ => "Fastest; supports tunnels, jump hosts, SFTP and resizing.",
        };
    }

    private void X11Box_Click(object sender, RoutedEventArgs e)
    {
        if (X11Box.IsChecked == true && Engine != TransportEngine.OpenSsh) SelectByTag(EngineBox, nameof(TransportEngine.OpenSsh));
        UpdateEngineState();
    }

    private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (X11Box is not null && EngineBox.SelectedItem is not null && Engine != TransportEngine.OpenSsh) X11Box.IsChecked = false;
        UpdateEngineState();
    }

    private void AuthMethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEngineState();
    private void UseJumpBox_Changed(object sender, RoutedEventArgs e) { }

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        AdvancedPanel.Visibility = sender.SelectedItem == AdvancedTab ? Visibility.Visible : Visibility.Collapsed;
        NetworkPanel.Visibility = sender.SelectedItem == NetworkTab ? Visibility.Visible : Visibility.Collapsed;
        BookmarkPanel.Visibility = sender.SelectedItem == BookmarkTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HostBox_TextChanged(object sender, TextChangedEventArgs e) => SuggestName(HostBox.Text.Trim());

    /// <summary>Fills the session name from the host (or shell) until the user types a name of their own.</summary>
    private void SuggestName(string name)
    {
        if (_nameTouched || NameBox is null) return;
        _suppressNameTracking = true;
        NameBox.Text = name;
        _suppressNameTracking = false;
    }

    private void ShellPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (ShellPicker.SelectedItem)
        {
            case ShellInfo sh:
                ShellPathBox.Text = sh.Path;
                ShellArgsBox.Text = sh.Arguments;
                ShellHint.Text = sh.Kind switch
                {
                    ShellKind.Cygwin => "Cygwin bash as a login shell - your Cygwin tools (ssh, nmap, openssl...) on PATH.",
                    ShellKind.GitBash => "Git for Windows' bash - ssh, scp, openssl, grep/sed/awk included.",
                    ShellKind.Msys2 => "MSYS2 UCRT64 login shell - install extra tools with pacman.",
                    _ => "",
                };
                SuggestName(sh.Name);
                break;
            case string:
                ShellHint.Text = "Any console program works: e.g. C:\\cygwin64\\bin\\bash.exe --login -i, or python.exe.";
                break;
        }
    }

    private void MatchShellPicker()
    {
        // Selecting fires SelectionChanged, which fills in that shell's default args - keep the saved ones.
        var (path, args) = (ShellPathBox.Text, ShellArgsBox.Text);
        var match = ShellPicker.Items.FirstOrDefault(i => i is ShellInfo sh && string.Equals(sh.Path, path, StringComparison.OrdinalIgnoreCase));
        if (match is not null) ShellPicker.SelectedItem = match;
        else if (path.Length > 0) ShellPicker.SelectedIndex = ShellPicker.Items.Count - 1;
        (ShellPathBox.Text, ShellArgsBox.Text) = (path, args);
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) KeyPathBox.Text = file.Path;
    }

    private async void BrowseShell_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) ShellPathBox.Text = file.Path;
    }

    private void FolderBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var text = sender.Text;
        sender.ItemsSource = string.IsNullOrEmpty(text)
            ? _folders
            : _folders.Where(f => f.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void FolderBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s) sender.Text = s;
    }

    private void OnCommit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var protocol = ProtocolOf(_kind);
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = protocol == ProtocolKind.Local ? (_kind == TileKind.Wsl ? "WSL" : "Local Shell")
                 : protocol == ProtocolKind.Serial ? SerialPortBox.Text.Trim()
                 : HostBox.Text.Trim();

        var profile = new SessionProfile
        {
            Id = _id,
            Name = name,
            Folder = FolderBox.Text.Trim(),
            Protocol = protocol,
            Engine = protocol == ProtocolKind.Ssh ? Engine : TransportEngine.BuiltIn,
            Host = HostBox.Text.Trim(),
            Port = double.IsNaN(PortBox.Value) ? 0 : (int)PortBox.Value,
            Username = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            AuthMethod = Enum.Parse<SshAuthMethod>(TagOf(AuthMethodBox)),
            PrivateKeyPath = KeyPathBox.Text.Trim(),
            PrivateKeyPassphrase = KeyPassphraseBox.Password,
            SerialPortName = SerialPortBox.Text.Trim(),
            BaudRate = int.TryParse(BaudBox.Text, out var baud) ? baud : 0,
            LocalShellPath = ShellPathBox.Text.Trim(),
            LocalShellArgs = ShellArgsBox.Text.Trim(),
            X11Forwarding = protocol == ProtocolKind.Ssh && X11Box.IsChecked == true,
            StartupCommands = StartupBox.Text.Trim(),
            Notes = NotesBox.Text.Trim(),
            Tags = string.Join(", ", TagsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)),
            ColorTag = SessionProfile.ColorTags[Math.Max(0, ColorBox.SelectedIndex)],
            UseJumpHost = UseJumpBox.IsChecked == true,
            JumpHost = JumpHostBox.Text.Trim(),
            JumpPort = double.IsNaN(JumpPortBox.Value) ? 0 : (int)JumpPortBox.Value,
            JumpUsername = JumpUserBox.Text.Trim(),
            JumpPassword = JumpPasswordBox.Password,
            CredentialId = SelectedCredential?.Id,
            EnablePassword = EnablePasswordBox.Password,
        };
        if (!profile.IsSshBased) profile.AuthMethod = SshAuthMethod.Password; // otherwise switching back to SSH later re-shows "Private key" with an empty path
        if (!profile.IsSshBased || profile.AuthMethod != SshAuthMethod.PrivateKey)
        {
            profile.PrivateKeyPath = "";
            profile.PrivateKeyPassphrase = "";
        }
        if (protocol != ProtocolKind.Local) { profile.LocalShellPath = ""; profile.LocalShellArgs = ""; }
        if (!profile.UseJumpHost) { profile.JumpHost = ""; profile.JumpUsername = ""; profile.JumpPassword = ""; }

        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            args.Cancel = true; // keep the dialog open
            ErrorBar.Message = string.Join("\n", errors);
            ErrorBar.IsOpen = true;
            if (errors.Any(e => e.StartsWith("Name"))) SectionBar.SelectedItem = BookmarkTab;
            else if (errors.Any(e => e.StartsWith("Jump"))) SectionBar.SelectedItem = NetworkTab;
            Result = null;
            return;
        }
        Result = profile;
    }

    /// <summary>Used by quick-connect and the Home tiles: seeds type/host/user without marking the name as user-typed.</summary>
    public void Prefill(SessionProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.Name)) { _suppressNameTracking = true; NameBox.Text = p.Name; _suppressNameTracking = false; }
        HostBox.Text = p.Host;
        UserBox.Text = p.Username;
        SelectTile(KindOf(p), resetPort: true);
        if (p.Port > 0) PortBox.Value = p.Port;
    }
}
