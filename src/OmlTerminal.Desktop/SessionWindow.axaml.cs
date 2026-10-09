using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Shells;

namespace OmlTerminal.Desktop;

/// <summary>Create or edit a saved session. Closes with the new/updated profile, or null when cancelled.
/// Editing works on a clone, so cancelling never changes the original.</summary>
public partial class SessionWindow : Window
{
    private static readonly ProtocolKind[] Protocols = [ProtocolKind.Ssh, ProtocolKind.Telnet, ProtocolKind.Serial, ProtocolKind.Local];
    private static readonly int[] BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    private readonly SessionProfile _profile;
    private readonly IReadOnlyList<ShellInfo> _shells = ShellCatalog.Detect();
    private ProtocolKind _lastProtocol;

    public SessionWindow() : this(null, []) { } // designer

    public SessionWindow(SessionProfile? existing, IReadOnlyList<string> folders)
    {
        InitializeComponent();
        _profile = existing?.Clone() ?? new SessionProfile();
        Title = existing is null ? "New Session" : $"Edit Session - {existing.Name}";
        SaveButton.Content = existing is null ? "Save" : "Save Changes";

        FolderBox.ItemsSource = folders;
        BaudBox.ItemsSource = BaudRates;
        try { SerialBox.ItemsSource = System.IO.Ports.SerialPort.GetPortNames().Order().ToList(); } catch { }
        ShellBox.ItemsSource = new[] { "Default shell" }.Concat(_shells.Select(s => $"{s.Name}  ({s.Path})")).ToList();

        var p = _profile;
        NameBox.Text = p.Name;
        FolderBox.Text = p.Folder;
        HostBox.Text = p.Host;
        PortBox.Value = p.Port;
        UserBox.Text = p.Username;
        PasswordBox.Text = p.Password;
        EnableBox.Text = p.EnablePassword;
        UseKeyBox.IsChecked = p.AuthMethod == SshAuthMethod.PrivateKey;
        KeyBox.Text = p.PrivateKeyPath;
        SerialBox.Text = p.SerialPortName;
        BaudBox.SelectedItem = BaudRates.Contains(p.BaudRate) ? p.BaudRate : 9600;
        StartupBox.Text = p.StartupCommands;
        int shellIndex = _shells.ToList().FindIndex(s => s.Path == p.LocalShellPath && s.Arguments == p.LocalShellArgs);
        if (shellIndex < 0) shellIndex = _shells.ToList().FindIndex(s => s.Path == p.LocalShellPath);
        ShellBox.SelectedIndex = string.IsNullOrEmpty(p.LocalShellPath) ? 0 : shellIndex + 1;

        _lastProtocol = Array.IndexOf(Protocols, p.Protocol) >= 0 ? p.Protocol : ProtocolKind.Ssh;
        ProtocolBox.SelectedIndex = Array.IndexOf(Protocols, _lastProtocol);
        ProtocolBox.SelectionChanged += (_, _) => OnProtocolChanged();
        UseKeyBox.IsCheckedChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
        Opened += (_, _) => NameBox.Focus();
    }

    private ProtocolKind SelectedProtocol => ProtocolBox.SelectedIndex is >= 0 and < 4 ? Protocols[ProtocolBox.SelectedIndex] : ProtocolKind.Ssh;

    private void OnProtocolChanged()
    {
        var now = SelectedProtocol;
        // Keep a custom port, but move a default one along with the protocol (22 <-> 23).
        if ((int?)PortBox.Value == SessionProfile.DefaultPortFor(_lastProtocol)) PortBox.Value = SessionProfile.DefaultPortFor(now);
        _lastProtocol = now;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var proto = SelectedProtocol;
        NetworkPanel.IsVisible = proto is ProtocolKind.Ssh or ProtocolKind.Telnet;
        SshPanel.IsVisible = proto == ProtocolKind.Ssh;
        KeyPanel.IsVisible = proto == ProtocolKind.Ssh && UseKeyBox.IsChecked == true;
        PasswordPanel.IsVisible = !(proto == ProtocolKind.Ssh && UseKeyBox.IsChecked == true);
        SerialPanel.IsVisible = proto == ProtocolKind.Serial;
        LocalPanel.IsVisible = proto == ProtocolKind.Local;
    }

    private async void BrowseKey_Click(object? sender, RoutedEventArgs e)
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var start = Directory.Exists(ssh) ? await StorageProvider.TryGetFolderFromPathAsync(ssh) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a private key",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) KeyBox.Text = path;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var p = _profile;
        p.Protocol = SelectedProtocol;
        p.Name = (NameBox.Text ?? "").Trim();
        p.Folder = (FolderBox.Text ?? "").Trim().Trim('/');
        p.StartupCommands = StartupBox.Text ?? "";
        if (p.Protocol is ProtocolKind.Ssh or ProtocolKind.Telnet)
        {
            p.Host = (HostBox.Text ?? "").Trim();
            p.Port = (int)(PortBox.Value ?? SessionProfile.DefaultPortFor(p.Protocol));
            p.Username = (UserBox.Text ?? "").Trim();
            p.EnablePassword = EnableBox.Text ?? "";
            bool key = p.Protocol == ProtocolKind.Ssh && UseKeyBox.IsChecked == true;
            p.AuthMethod = key ? SshAuthMethod.PrivateKey : SshAuthMethod.Password;
            p.PrivateKeyPath = key ? ExpandHome((KeyBox.Text ?? "").Trim()) : p.PrivateKeyPath;
            p.Password = key ? "" : PasswordBox.Text ?? "";
        }
        else if (p.Protocol == ProtocolKind.Serial)
        {
            p.SerialPortName = (SerialBox.Text ?? "").Trim();
            p.BaudRate = BaudBox.SelectedItem is int b ? b : 9600;
        }
        else if (p.Protocol == ProtocolKind.Local)
        {
            var shell = ShellBox.SelectedIndex > 0 ? _shells[ShellBox.SelectedIndex - 1] : null;
            p.LocalShellPath = shell?.Path ?? "";
            p.LocalShellArgs = shell?.Arguments ?? "";
        }
        if (string.IsNullOrWhiteSpace(p.Name))
            p.Name = p.Protocol switch
            {
                ProtocolKind.Serial => p.SerialPortName,
                ProtocolKind.Local => "Local Shell",
                _ => p.Host,
            };

        var errors = p.Validate();
        if (errors.Count > 0)
        {
            ErrorText.Text = string.Join("\n", errors);
            ErrorText.IsVisible = true;
            return;
        }
        Close(p);
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
