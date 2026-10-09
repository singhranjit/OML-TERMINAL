using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Sftp;
using OmlTerminal.Desktop.Tools;

namespace OmlTerminal.Desktop;

/// <summary>Browse, upload, download, edit, rename and delete files over SFTP on an SSH session.</summary>
public sealed class SftpWindow : Window
{
    private const int MaxPathHistory = 20;

    private readonly SftpSession _sftp;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Guid _profileId;
    private readonly List<RemoteEditSession> _editSessions = new();
    private string _path = "/";

    private readonly TextBox _pathBox = Ui.Input("", "/", mono: true);
    private readonly DataGrid _list;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private readonly TextBlock _status = Ui.Text("Connecting…", 12, color: Ui.Muted);
    private readonly Button _recent;

    public SftpWindow(SessionProfile profile, AppSettings settings, Action saveSettings)
    {
        Title = $"SFTP - {profile.Name}";
        Width = 820;
        Height = 560;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _sftp = SftpSession.For(profile);
        _settings = settings;
        _saveSettings = saveSettings;
        _profileId = profile.Id;

        _list = Ui.Table(
            Ui.Col<SftpEntry>("Name", e => (e.IsDirectory ? "▸ " : "   ") + e.Name, fill: true, color: e => e.IsDirectory ? Ui.Sky : null, mono: true,
                sortKey: e => (e.IsDirectory ? "0" : "1") + e.Name.ToLowerInvariant()),
            Ui.Col<SftpEntry>("Size", e => e.IsDirectory ? "" : FormatSize(e.Size), 100, sortKey: e => e.IsDirectory ? -1 : e.Size),
            Ui.Col<SftpEntry>("Modified", e => e.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 175, sortKey: e => e.Modified));
        _list.SelectionMode = DataGridSelectionMode.Single;
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem is SftpEntry e) Open(e); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is SftpEntry en) { e.Handled = true; Open(en); }
            else if (e.Key == Key.Back) { e.Handled = true; Navigate(SftpSession.Parent(_path)); }
            else if (e.Key == Key.Delete && _list.SelectedItem is SftpEntry del) { e.Handled = true; _ = DeleteAsync(del); }
        };
        _list.ContextMenu = BuildContextMenu();
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        _list.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is null) return;
            await UploadFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().Where(File.Exists));
        });

        _pathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Navigate((_pathBox.Text ?? "").Trim()); } };
        _recent = Ui.Button("Recent ▾", ShowRecent, tip: "Recently visited folders on this session");
        _banner = Ui.Banner(out _bannerText, Ui.Rose);

        var top = Ui.Columns("Auto,*,Auto", Ui.Button("Up", () => Navigate(SftpSession.Parent(_path)), tip: "Parent folder (Backspace)"), _pathBox,
            Ui.Button("Go", () => Navigate((_pathBox.Text ?? "").Trim())));
        var actions = Ui.Row(Ui.Button("New folder…", () => _ = NewFolderAsync()), Ui.Button("Upload…", () => _ = UploadAsync()),
            Ui.Button("Download…", () => { if (_list.SelectedItem is SftpEntry { IsDirectory: false } e) _ = DownloadAsync(e); }),
            Ui.Button("Refresh", () => _ = LoadAsync()), _recent,
            Ui.Text("Tip: drag files here from your file manager to upload.", 11, color: Ui.Muted));
        var dock = new DockPanel { Margin = new Thickness(14) };
        foreach (var (c, d) in new (Control, Dock)[] { (top, Dock.Top), (actions, Dock.Top), (_banner, Dock.Top), (_status, Dock.Bottom) })
        {
            DockPanel.SetDock(c, d);
            c.Margin = new Thickness(0, 0, 0, 8);
            dock.Children.Add(c);
        }
        _status.Margin = new Thickness(0, 8, 0, 0);
        dock.Children.Add(_list);
        Content = dock;

        Opened += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            foreach (var e in _editSessions) e.DeleteLocalCopy();
            _sftp.Dispose();
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    private void Error(string? message) => Ui.Show(_banner, _bannerText, message);

    /// <summary>Connects and opens the last folder visited on this session if it's still reachable, otherwise the home folder.</summary>
    private async Task InitializeAsync()
    {
        try
        {
            await _sftp.ConnectAsync();
            var home = string.IsNullOrEmpty(_sftp.WorkingDirectory) ? "/" : _sftp.WorkingDirectory;
            _path = home;
            if (_settings.SftpPathHistory.TryGetValue(_profileId, out var history) && history.Count > 0)
            {
                try { await _sftp.ListAsync(history[0]); _path = history[0]; }
                catch { _path = home; }
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error($"Could not connect: {ex.Message}");
            _status.Text = "Not connected";
        }
    }

    private async Task LoadAsync()
    {
        Error(null);
        _status.Text = "Loading…";
        try
        {
            _pathBox.Text = _path;
            var entries = await _sftp.ListAsync(_path);
            _list.ItemsSource = entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _status.Text = $"{_path} · {entries.Count(e => e.IsDirectory)} folder(s), {entries.Count(e => !e.IsDirectory)} file(s)";
            RecordVisit(_path);
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            _status.Text = "";
        }
    }

    private void RecordVisit(string path)
    {
        if (!_settings.SftpPathHistory.TryGetValue(_profileId, out var history))
            _settings.SftpPathHistory[_profileId] = history = new List<string>();
        history.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
        history.Insert(0, path);
        if (history.Count > MaxPathHistory) history.RemoveRange(MaxPathHistory, history.Count - MaxPathHistory);
        try { _saveSettings(); } catch { }
    }

    private void ShowRecent()
    {
        var items = new List<Control>();
        if (_settings.SftpPathHistory.TryGetValue(_profileId, out var history) && history.Count > 0)
            foreach (var path in history)
            {
                var mi = new MenuItem { Header = path, IsEnabled = path != _path };
                mi.Click += (_, _) => Navigate(path);
                items.Add(mi);
            }
        else items.Add(new MenuItem { Header = "(no recent folders yet)", IsEnabled = false });
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(_recent);
    }

    private void Navigate(string path)
    {
        if (path.Length == 0) return;
        _path = path;
        _ = LoadAsync();
    }

    private void Open(SftpEntry e)
    {
        if (e.IsDirectory) Navigate(e.FullPath);
        else _ = EditAsync(e);
    }

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string header, Func<SftpEntry, Task> act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += async (_, _) => { if (_list.SelectedItem is SftpEntry e) await act(e); };
            return mi;
        }
        var open = Item("Open", e => { Open(e); return Task.CompletedTask; });
        var download = Item("Download…", DownloadAsync);
        var edit = Item("Edit", EditAsync);
        var copyPath = Item("Copy path", e => ToolUi.CopyAsync(e.FullPath));
        var rename = Item("Rename…", RenameAsync);
        var delete = Item("Delete", DeleteAsync);
        var menu = new ContextMenu { ItemsSource = new Control[] { open, download, edit, copyPath, new Separator(), rename, delete } };
        menu.Opening += (_, e) =>
        {
            var sel = _list.SelectedItem as SftpEntry;
            if (sel is null) { e.Cancel = true; return; }
            download.IsVisible = edit.IsVisible = !sel.IsDirectory;
        };
        return menu;
    }

    /// <summary>Downloads to a private temp copy, opens it in the desktop's default app, and offers to upload it back.</summary>
    private async Task EditAsync(SftpEntry entry)
    {
        try
        {
            // entry.Name comes from the server's listing - GetFileName strips any "../" a hostile server might send
            // before it touches a local path.
            var safeName = Path.GetFileName(entry.Name) is { Length: > 0 } n ? n : "file";
            var tempDir = Path.Combine(Path.GetTempPath(), "oml-sftp-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tempDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var tempPath = Path.Combine(tempDir, safeName);
            await using (var f = File.Create(tempPath)) await _sftp.DownloadAsync(entry.FullPath, f);

            var edit = new RemoteEditSession(_sftp, entry.FullPath, tempPath);
            _editSessions.Add(edit);
            Launch(tempPath);
            if (await Dialogs.ConfirmAsync(this, $"Editing {entry.Name}",
                    $"Opened {entry.Name} in your default editor. Save it there, then click Save back to upload your changes.", "Save back to server"))
            {
                await edit.SaveBackAsync();
                await LoadAsync();
                _status.Text = $"Uploaded changes to {entry.FullPath}";
            }
            edit.DeleteLocalCopy();
            _editSessions.Remove(edit);
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private static void Launch(string path)
    {
        var psi = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [path])
            : OperatingSystem.IsWindows() ? new ProcessStartInfo(path) { UseShellExecute = true }
            : new ProcessStartInfo("xdg-open", [path]);
        Process.Start(psi)?.Dispose();
    }

    private async Task DownloadAsync(SftpEntry entry)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = entry.Name, Title = "Download" });
        if (file?.TryGetLocalPath() is not { } local) return;
        try
        {
            _status.Text = $"Downloading {entry.Name}…";
            await using (var stream = File.Create(local)) await _sftp.DownloadAsync(entry.FullPath, stream);
            _status.Text = $"Downloaded {entry.Name} to {local}";
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private async Task UploadAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "Upload to " + _path });
        await UploadFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async Task UploadFilesAsync(IEnumerable<string> localPaths)
    {
        int done = 0;
        foreach (var local in localPaths)
        {
            try
            {
                _status.Text = $"Uploading {Path.GetFileName(local)}…";
                await using var f = File.OpenRead(local);
                await _sftp.UploadAsync(f, SftpSession.Combine(_path, Path.GetFileName(local)));
                done++;
            }
            catch (Exception ex) { Error($"{Path.GetFileName(local)}: {ex.Message}"); }
        }
        await LoadAsync();
        if (done > 0) _status.Text = $"Uploaded {done} file(s) to {_path}";
    }

    private async Task RenameAsync(SftpEntry entry)
    {
        var name = await Dialogs.PromptAsync(this, "Rename", $"New name for {entry.Name}:", okText: "Rename", initial: entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == entry.Name) return;
        try
        {
            await _sftp.RenameAsync(entry.FullPath, SftpSession.Combine(SftpSession.Parent(entry.FullPath), name.Trim()));
            await LoadAsync();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private async Task DeleteAsync(SftpEntry entry)
    {
        if (!await Dialogs.ConfirmAsync(this, "Delete", $"Delete {entry.Name}? This can't be undone.", "Delete")) return;
        try
        {
            if (entry.IsDirectory) await _sftp.DeleteDirectoryAsync(entry.FullPath);
            else await _sftp.DeleteFileAsync(entry.FullPath);
            await LoadAsync();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private async Task NewFolderAsync()
    {
        var name = await Dialogs.PromptAsync(this, "New folder", $"Create a folder in {_path}:", okText: "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _sftp.CreateDirectoryAsync(SftpSession.Combine(_path, name.Trim()));
            await LoadAsync();
        }
        catch (Exception ex) { Error(ex.Message); }
    }
}
