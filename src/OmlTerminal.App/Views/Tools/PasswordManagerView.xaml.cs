using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.App.ViewModels;
using OmlTerminal.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace OmlTerminal.App.Views.Tools;

public sealed class CredentialRow(Credential c, int users)
{
    public Credential Credential { get; } = c;
    public string Name => Credential.Name;
    public string Detail => $"{(Credential.Username.Length > 0 ? Credential.Username : "(no username)")}{(Credential.EnablePassword.Length > 0 ? " · enable" : "")} · {users} session{(users == 1 ? "" : "s")}";
}

public sealed class LinkRow(SessionProfile p, string credentialName)
{
    public SessionProfile Profile { get; } = p;
    public string Detail => $"{Profile.Host}{(credentialName.Length > 0 ? $"  ·  uses {credentialName}" : "")}";
}

public sealed partial class PasswordManagerView : UserControl, IToolView
{
    private readonly MainViewModel _vm;
    private Credential? _editing;
    private readonly HashSet<Guid> _linked = new();
    private bool _refreshingLinks, _shownOnce;

    public PasswordManagerView(ToolContext ctx)
    {
        _vm = ctx.Model;
        InitializeComponent();
        SecurityBar.Severity = _vm.Settings.HasMasterPassword ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        SecurityBar.Title = _vm.Settings.HasMasterPassword ? "Encrypted with your master password (AES-256)" : "Encrypted with your Windows account (DPAPI)";
        SecurityBar.Message = _vm.Settings.HasMasterPassword
            ? "Nothing in the vault is readable without the master password."
            : "Only you, on this PC, can read the vault. Set a master password in Settings for a stronger key that also covers session passwords.";
        // Loaded fires again on every tab switch - only start a blank form the first time, or an edit in progress is lost.
        Loaded += (_, _) =>
        {
            RefreshList(_editing?.Id);
            if (!_shownOnce) { _shownOnce = true; StartNew(); }
        };
    }

    private void RefreshList(Guid? select = null)
    {
        var q = FilterBox.Text.Trim();
        var rows = _vm.Credentials
            .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Username.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(c => new CredentialRow(c, _vm.SessionsUsing(c.Id))).ToList();
        CredList.ItemsSource = rows;
        if (select is { } id) CredList.SelectedItem = rows.FirstOrDefault(r => r.Credential.Id == id);
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => RefreshList(_editing?.Id);

    private void New_Click(object sender, RoutedEventArgs e)
    {
        CredList.SelectedItem = null;
        StartNew();
        NameBox.Focus(FocusState.Programmatic);
    }

    private void StartNew()
    {
        _editing = null;
        EditorTitle.Text = "NEW CREDENTIAL";
        NameBox.Text = UserBox.Text = NotesBox.Text = "";
        PasswordBox.Password = EnableBox.Password = "";
        DeleteButton.IsEnabled = false;
        LoadLinks();
    }

    private void CredList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CredList.SelectedItem is not CredentialRow row) return;
        var c = row.Credential;
        _editing = c;
        EditorTitle.Text = $"EDIT · {c.Name.ToUpperInvariant()}";
        NameBox.Text = c.Name;
        UserBox.Text = c.Username;
        PasswordBox.Password = c.Password;
        EnableBox.Password = c.EnablePassword;
        NotesBox.Text = c.Notes;
        DeleteButton.IsEnabled = true;
        StatusText.Text = $"Last changed {c.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        LoadLinks();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "Give the credential a name."; return; }
        if (_vm.Credentials.Any(c => c.Id != _editing?.Id && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"A credential called '{name}' already exists.";
            return;
        }
        var c = _editing?.Clone() ?? new Credential();
        c.Name = name;
        c.Username = UserBox.Text.Trim();
        c.Password = PasswordBox.Password;
        c.EnablePassword = EnableBox.Password;
        c.Notes = NotesBox.Text.Trim();
        try { _vm.SaveCredential(c); }
        catch (Exception ex) { StatusText.Text = $"Could not save: {ex.Message}"; return; }
        _editing = c;
        RefreshList(c.Id);
        StatusText.Text = $"Saved '{c.Name}'. Tick sessions on the right and Apply links to use it.";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } c) return;
        int users = _vm.SessionsUsing(c.Id);
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["AppContentDialogStyle"],
            Title = $"Delete '{c.Name}'?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = users == 0
                    ? "No sessions use it."
                    : $"{users} session(s) use it. They keep working: its username and password are copied back onto each of them.",
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try { _vm.DeleteCredential(c); }
        catch (Exception ex) { StatusText.Text = $"Could not delete: {ex.Message}"; return; }
        RefreshList();
        StartNew();
        StatusText.Text = $"Deleted '{c.Name}'.";
    }

    // ---------- linked sessions

    private void LoadLinks()
    {
        _linked.Clear();
        if (_editing is { } c) foreach (var s in _vm.Sessions.Where(s => s.CredentialId == c.Id)) _linked.Add(s.Id);
        ShowLinks();
        ApplyLinksButton.IsEnabled = false;
    }

    private void ShowLinks()
    {
        var q = SessionFilter.Text.Trim();
        var names = _vm.Credentials.ToDictionary(c => c.Id, c => c.Name);
        var rows = _vm.Sessions
            .Where(s => s.Protocol != ProtocolKind.Local && s.Protocol != ProtocolKind.Serial)
            .Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || s.Folder.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .Select(s => new LinkRow(s, s.CredentialId is { } id && id != _editing?.Id && names.TryGetValue(id, out var n) ? n : ""))
            .ToList();
        _refreshingLinks = true;
        SessionList.ItemsSource = rows;
        SessionList.IsEnabled = _editing is not null;
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var r in rows.Where(r => _linked.Contains(r.Profile.Id))) SessionList.SelectedItems.Add(r);
            _refreshingLinks = false;
            UpdateLinkCount();
        });
    }

    private void SessionFilter_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ShowLinks();

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingLinks) return;
        foreach (var r in e.AddedItems.OfType<LinkRow>()) _linked.Add(r.Profile.Id);
        foreach (var r in e.RemovedItems.OfType<LinkRow>()) _linked.Remove(r.Profile.Id);
        ApplyLinksButton.IsEnabled = _editing is not null;
        UpdateLinkCount();
    }

    private void UpdateLinkCount()
    {
        LinkTitle.Text = _editing is null ? "USED BY SESSIONS" : $"USED BY SESSIONS · {_editing.Name.ToUpperInvariant()}";
        LinkCount.Text = _editing is null ? "Save the credential first, then pick the sessions that log in with it." : $"{_linked.Count} session(s) ticked";
    }

    private void ApplyLinks_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } c) return;
        var link = _vm.Sessions.Where(s => _linked.Contains(s.Id)).ToList();
        var unlink = _vm.Sessions.Where(s => s.CredentialId == c.Id && !_linked.Contains(s.Id)).ToList();
        try
        {
            _vm.AssignCredential(c.Id, link);
            _vm.AssignCredential(null, unlink);
        }
        catch (Exception ex) { StatusText.Text = $"Could not update sessions: {ex.Message}"; return; }
        ApplyLinksButton.IsEnabled = false;
        RefreshList(c.Id);
        StatusText.Text = $"'{c.Name}' is now used by {link.Count} session(s){(unlink.Count > 0 ? $"; {unlink.Count} unlinked" : "")}.";
    }

    // ---------- clipboard

    private void CopyUser_Click(object sender, RoutedEventArgs e) => ToolUi.Copy(UserBox.Text);
    private void CopyPassword_Click(object sender, RoutedEventArgs e) => CopySecret(PasswordBox.Password, "Password");
    private void CopyEnable_Click(object sender, RoutedEventArgs e) => CopySecret(EnableBox.Password, "Enable password");

    /// <summary>Copies a secret, then wipes the clipboard after 30 s - unless something else was copied meanwhile.</summary>
    private async void CopySecret(string secret, string what)
    {
        if (secret.Length == 0) return;
        ToolUi.Copy(secret);
        StatusText.Text = $"{what} copied - the clipboard clears in 30 seconds.";
        await Task.Delay(TimeSpan.FromSeconds(30));
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text) && await content.GetTextAsync() == secret) Clipboard.Clear();
        }
        catch { }
    }

    public void Shutdown() { }
}
