using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using OmlTerminal.App.ViewModels;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Desktop.Tools;

public sealed class PasswordManagerTool : UserControl, IToolView
{
    private sealed record CredRow(Credential Credential, int Users)
    {
        public string Detail => $"{(Credential.Username.Length > 0 ? Credential.Username : "(no username)")}{(Credential.EnablePassword.Length > 0 ? " · enable" : "")} · {Users} session{(Users == 1 ? "" : "s")}";
    }

    private sealed record LinkRow(SessionProfile Profile, string OtherCredential);

    private readonly MainViewModel _vm;
    private Credential? _editing;
    private readonly HashSet<Guid> _linked = new();
    private bool _refreshingLinks;

    private readonly TextBox _filter = Ui.Input("", "Search credentials");
    private readonly ListBox _creds = new() { Background = Brushes.Transparent };
    private readonly TextBlock _editorTitle = Ui.Section("New credential");
    private readonly TextBox _name = Ui.Input("", "e.g. TACACS netops, Branch firewalls, lab-admin");
    private readonly TextBox _user = Ui.Input("", mono: true);
    private readonly TextBox _password = new() { PasswordChar = '•', RevealPassword = false };
    private readonly TextBox _enable = new() { PasswordChar = '•', RevealPassword = false };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80, PlaceholderText = "Rotation date, owner, ticket..." };
    private readonly TextBlock _status = Ui.Text("", 12, color: Ui.Muted);
    private readonly Button _delete, _applyLinks;
    private readonly TextBlock _linkTitle = Ui.Section("Used by sessions"), _linkCount = Ui.Text("", 12, color: Ui.Muted);
    private readonly TextBox _sessionFilter = Ui.Input("", "Filter sessions by name, folder, host");
    private readonly ListBox _sessions = new() { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, Background = Brushes.Transparent };

    public PasswordManagerTool(ToolContext ctx)
    {
        _vm = ctx.Model;
        _creds.ItemTemplate = new FuncDataTemplate<CredRow>((r, _) => r is null ? new Panel() : Ui.Stack(1,
            new TextBlock { Text = r.Credential.Name, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = r.Detail, FontSize = 11.5, Foreground = Ui.Muted }));
        _sessions.ItemTemplate = new FuncDataTemplate<LinkRow>((r, _) => r is null ? new Panel() : Ui.Stack(1,
            new TextBlock { Text = r.Profile.Display, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = $"{r.Profile.Host}{(r.OtherCredential.Length > 0 ? $"  ·  uses {r.OtherCredential}" : "")}", FontSize = 11.5, Foreground = Ui.Muted }));
        _filter.TextChanged += (_, _) => RefreshList(_editing?.Id);
        _creds.SelectionChanged += (_, _) => { if (_creds.SelectedItem is CredRow r) Edit(r.Credential); };
        _sessionFilter.TextChanged += (_, _) => ShowLinks();
        _sessions.SelectionChanged += (_, e) =>
        {
            if (_refreshingLinks) return;
            foreach (var r in e.AddedItems.OfType<LinkRow>()) _linked.Add(r.Profile.Id);
            foreach (var r in e.RemovedItems.OfType<LinkRow>()) _linked.Remove(r.Profile.Id);
            _applyLinks.IsEnabled = _editing is not null;
            UpdateLinkCount();
        };
        _delete = Ui.Button("Delete", () => _ = DeleteAsync());
        _applyLinks = Ui.Button("Apply links", ApplyLinks, accent: true);

        bool master = _vm.Settings.HasMasterPassword;
        var banner = Ui.Banner(out var bannerText, master ? Ui.Mint : Ui.Sky);
        Ui.Show(banner, bannerText, master
            ? "Encrypted with your master password (AES-256). Nothing in the vault is readable without it."
            : OperatingSystem.IsWindows()
                ? "Encrypted with your Windows account (DPAPI). Set a master password in Settings for a stronger key."
                : "Encrypted with a key only your user account can read. Set a master password in Settings for a key that never touches the disk.");

        var left = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(8, new DockPanel { Children = { WithDock(Ui.Button("New", StartNew, accent: true), Dock.Right), Ui.Section("Credentials") } }, _filter), Dock.Top),
                new ScrollViewer { Content = _creds, Margin = new Thickness(0, 8, 0, 0) },
            },
        });
        var editor = Ui.Card(new ScrollViewer
        {
            Content = Ui.Stack(12, _editorTitle,
                Ui.Field("Name *", _name),
                Ui.Columns("*,Auto", Ui.Field("Username", _user), Ui.Button("Copy", () => ToolUi.Copy(_user.Text ?? ""))),
                Ui.Columns("*,Auto,Auto", Ui.Field("Password", _password), Reveal(_password), Ui.Button("Copy", () => _ = CopySecretAsync(_password.Text ?? "", "Password"))),
                Ui.Columns("*,Auto,Auto", Ui.Field("Enable password (Cisco privileged mode - optional)", _enable), Reveal(_enable), Ui.Button("Copy", () => _ = CopySecretAsync(_enable.Text ?? "", "Enable password"))),
                Ui.Field("Notes", _notes),
                Ui.Row(Ui.Button("Save", Save, accent: true), _delete),
                _status),
        });
        var links = Ui.Card(new DockPanel
        {
            Children =
            {
                WithDock(Ui.Stack(8, _linkTitle, _sessionFilter), Dock.Top),
                WithDock(new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { WithDock(_applyLinks, Dock.Right), _linkCount } }, Dock.Bottom),
                new ScrollViewer { Content = _sessions, Margin = new Thickness(0, 8, 0, 0) },
            },
        });
        _linkCount.VerticalAlignment = VerticalAlignment.Center;
        var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("260,*,*"), ColumnSpacing = 16 };
        body.Children.Add(left);
        Grid.SetColumn(editor, 1);
        body.Children.Add(editor);
        Grid.SetColumn(links, 2);
        body.Children.Add(links);
        Content = Ui.Page("Password Manager", "Saved logins and enable passwords, shared by sessions, backups and every SSH tool. Change a password once and every linked session uses it.", body, banner);
        RefreshList();
        StartNew();
    }

    private static T WithDock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    private static Avalonia.Controls.Button Reveal(TextBox box) => Ui.Button("Show", () => box.RevealPassword = !box.RevealPassword, tip: "Show or hide");

    private void RefreshList(Guid? select = null)
    {
        var q = (_filter.Text ?? "").Trim();
        var rows = _vm.Credentials
            .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Username.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(c => new CredRow(c, _vm.SessionsUsing(c.Id))).ToList();
        _creds.ItemsSource = rows;
        if (select is { } id) _creds.SelectedItem = rows.FirstOrDefault(r => r.Credential.Id == id);
    }

    private void StartNew()
    {
        _creds.SelectedItem = null;
        _editing = null;
        _editorTitle.Text = "NEW CREDENTIAL";
        _name.Text = _user.Text = _notes.Text = _password.Text = _enable.Text = "";
        _delete.IsEnabled = false;
        _status.Text = "";
        LoadLinks();
    }

    private void Edit(Credential c)
    {
        _editing = c;
        _editorTitle.Text = $"EDIT · {c.Name.ToUpperInvariant()}";
        _name.Text = c.Name;
        _user.Text = c.Username;
        _password.Text = c.Password;
        _enable.Text = c.EnablePassword;
        _notes.Text = c.Notes;
        _delete.IsEnabled = true;
        _status.Text = $"Last changed {c.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        LoadLinks();
    }

    private void Save()
    {
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) { _status.Text = "Give the credential a name."; return; }
        if (_vm.Credentials.Any(c => c.Id != _editing?.Id && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _status.Text = $"A credential called '{name}' already exists.";
            return;
        }
        var c = _editing?.Clone() ?? new Credential();
        c.Name = name;
        c.Username = (_user.Text ?? "").Trim();
        c.Password = _password.Text ?? "";
        c.EnablePassword = _enable.Text ?? "";
        c.Notes = (_notes.Text ?? "").Trim();
        try { _vm.SaveCredential(c); }
        catch (Exception ex) { _status.Text = $"Could not save: {ex.Message}"; return; }
        _editing = c;
        RefreshList(c.Id);
        _status.Text = $"Saved '{c.Name}'. Tick sessions on the right and Apply links to use it.";
    }

    private async Task DeleteAsync()
    {
        if (_editing is not { } c || MainWindow.Current is not { } owner) return;
        int users = _vm.SessionsUsing(c.Id);
        if (!await Dialogs.ConfirmAsync(owner, $"Delete '{c.Name}'?", users == 0
                ? "No sessions use it."
                : $"{users} session(s) use it. They keep working: its username and password are copied back onto each of them.", "Delete"))
            return;
        try { _vm.DeleteCredential(c); }
        catch (Exception ex) { _status.Text = $"Could not delete: {ex.Message}"; return; }
        RefreshList();
        StartNew();
        _status.Text = $"Deleted '{c.Name}'.";
    }

    private void LoadLinks()
    {
        _linked.Clear();
        if (_editing is { } c) foreach (var s in _vm.Sessions.Where(s => s.CredentialId == c.Id)) _linked.Add(s.Id);
        ShowLinks();
        _applyLinks.IsEnabled = false;
    }

    private void ShowLinks()
    {
        var q = (_sessionFilter.Text ?? "").Trim();
        var names = _vm.Credentials.ToDictionary(c => c.Id, c => c.Name);
        var rows = _vm.Sessions
            .Where(s => s.Protocol != ProtocolKind.Local && s.Protocol != ProtocolKind.Serial)
            .Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || s.Folder.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .Select(s => new LinkRow(s, s.CredentialId is { } id && id != _editing?.Id && names.TryGetValue(id, out var n) ? n : ""))
            .ToList();
        _refreshingLinks = true;
        _sessions.ItemsSource = rows;
        _sessions.IsEnabled = _editing is not null;
        for (int i = 0; i < rows.Count; i++) if (_linked.Contains(rows[i].Profile.Id)) _sessions.Selection.Select(i);
        _refreshingLinks = false;
        UpdateLinkCount();
    }

    private void UpdateLinkCount()
    {
        _linkTitle.Text = _editing is null ? "USED BY SESSIONS" : $"USED BY SESSIONS · {_editing.Name.ToUpperInvariant()}";
        _linkCount.Text = _editing is null ? "Save the credential first, then pick the sessions that log in with it." : $"{_linked.Count} session(s) ticked";
    }

    private void ApplyLinks()
    {
        if (_editing is not { } c) return;
        var link = _vm.Sessions.Where(s => _linked.Contains(s.Id)).ToList();
        var unlink = _vm.Sessions.Where(s => s.CredentialId == c.Id && !_linked.Contains(s.Id)).ToList();
        try
        {
            _vm.AssignCredential(c.Id, link);
            _vm.AssignCredential(null, unlink);
        }
        catch (Exception ex) { _status.Text = $"Could not update sessions: {ex.Message}"; return; }
        _applyLinks.IsEnabled = false;
        RefreshList(c.Id);
        _status.Text = $"'{c.Name}' is now used by {link.Count} session(s){(unlink.Count > 0 ? $"; {unlink.Count} unlinked" : "")}.";
    }

    /// <summary>Copies a secret, then wipes the clipboard after 30 s - unless something else was copied meanwhile.</summary>
    private async Task CopySecretAsync(string secret, string what)
    {
        if (secret.Length == 0 || MainWindow.Current?.Clipboard is not { } clip) return;
        await ToolUi.CopyAsync(secret);
        _status.Text = $"{what} copied - the clipboard clears in 30 seconds.";
        await Task.Delay(TimeSpan.FromSeconds(30));
        try { if (await clip.TryGetTextAsync() == secret) await clip.ClearAsync(); } catch { }
    }

    public void Shutdown() { }
}
