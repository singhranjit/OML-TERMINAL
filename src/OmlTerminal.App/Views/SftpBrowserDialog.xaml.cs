using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Sftp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace OmlTerminal.App.Views;

/// <summary>Display-only wrapper so the XAML template can x:Bind to formatted strings without putting WinUI types in Core.</summary>
public sealed class SftpEntryView(SftpEntry entry)
{
    public SftpEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public bool IsDirectory => entry.IsDirectory;
    public string SizeText => entry.IsDirectory ? "" : FormatSize(entry.Size);
    public string Modified => entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public Visibility DirIconVisibility => entry.IsDirectory ? Visibility.Visible : Visibility.Collapsed;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}

public sealed partial class SftpBrowserDialog : ContentDialog
{
    private const int MaxPathHistory = 20;

    private readonly SftpSession _sftp;
    private readonly List<RemoteEditSession> _editSessions = new();
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Guid _profileId;
    private string _path = "/";

    public SftpBrowserDialog(XamlRoot xamlRoot, SessionProfile profile, AppSettings settings, Action saveSettings)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        Title = $"SFTP - {profile.Name}";
        _sftp = SftpSession.For(profile);
        _settings = settings;
        _saveSettings = saveSettings;
        _profileId = profile.Id;
        Closed += (_, _) => { foreach (var e in _editSessions) e.DeleteLocalCopy(); _sftp.Dispose(); };
    }

    /// <summary>Connects and loads the initial directory: the last folder visited on this session, if it's still
    /// reachable, otherwise the server's home directory.</summary>
    public async Task<bool> InitializeAsync()
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
            return true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"Could not connect: {ex.Message}";
            ErrorBar.IsOpen = true;
            return false;
        }
    }

    private async Task LoadAsync()
    {
        Busy.IsActive = true;
        ErrorBar.IsOpen = false;
        try
        {
            PathBox.Text = _path;
            var entries = await _sftp.ListAsync(_path);
            FileList.ItemsSource = entries.Select(e => new SftpEntryView(e)).ToList();
            RecordVisit(_path);
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally { Busy.IsActive = false; }
    }

    private void RecordVisit(string path)
    {
        if (!_settings.SftpPathHistory.TryGetValue(_profileId, out var history))
        {
            history = new List<string>();
            _settings.SftpPathHistory[_profileId] = history;
        }
        history.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
        history.Insert(0, path);
        if (history.Count > MaxPathHistory) history.RemoveRange(MaxPathHistory, history.Count - MaxPathHistory);
        _saveSettings();
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        if (_settings.SftpPathHistory.TryGetValue(_profileId, out var history) && history.Count > 0)
        {
            foreach (var path in history)
            {
                var item = new MenuFlyoutItem { Text = path, IsEnabled = path != _path };
                item.Click += (_, _) => NavigateTo(path);
                menu.Items.Add(item);
            }
        }
        else
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "(no recent folders yet)", IsEnabled = false });
        }
        menu.ShowAt((FrameworkElement)sender);
    }

    private async void NavigateTo(string path) { _path = path; await LoadAsync(); }

    private void Up_Click(object sender, RoutedEventArgs e) => NavigateTo(SftpSession.Parent(_path));
    private void Go_Click(object sender, RoutedEventArgs e) => NavigateTo(PathBox.Text.Trim());
    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) NavigateTo(PathBox.Text.Trim()); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SftpEntryView entry) return;
        if (entry.IsDirectory) NavigateTo(entry.Entry.FullPath);
        else _ = EditAsync(entry.Entry);
    }

    private async Task EditAsync(SftpEntry entry)
    {
        try
        {
            // entry.Name comes straight from the server's directory listing - Path.GetFileName strips any
            // "../" traversal it might contain before it ever touches a local path (a malicious or compromised
            // SFTP server could otherwise plant/overwrite an arbitrary file outside this temp folder).
            var safeName = Path.GetFileName(entry.Name) is { Length: > 0 } n ? n : "file";
            var tempDir = Path.Combine(Path.GetTempPath(), "oml-sftp-" + Guid.NewGuid());
            var tempPath = Path.Combine(tempDir, safeName);
            Directory.CreateDirectory(tempDir);
            await using (var f = File.Create(tempPath)) await _sftp.DownloadAsync(entry.FullPath, f);

            var edit = new RemoteEditSession(_sftp, entry.FullPath, tempPath);
            _editSessions.Add(edit);
            await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(tempPath));

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ElementTheme.Dark,
                Title = $"Editing {entry.Name}",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Opened in your default editor for {entry.Name}. Click Save back when you're done editing, to upload your changes." },
                PrimaryButtonText = "Save back to server",
                CloseButtonText = "Done (discard local copy)",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await edit.SaveBackAsync();
                await LoadAsync();
            }
            edit.DeleteLocalCopy();
            _editSessions.Remove(edit);
        }
        catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
    }

    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SftpEntryView entry) return;
        FileList.SelectedItem = entry;
        var menu = new MenuFlyout();

        if (!entry.IsDirectory)
        {
            var download = new MenuFlyoutItem { Text = "Download..." };
            download.Click += async (_, _) => await DownloadAsync(entry.Entry);
            menu.Items.Add(download);

            var edit = new MenuFlyoutItem { Text = "Edit" };
            edit.Click += async (_, _) => await EditAsync(entry.Entry);
            menu.Items.Add(edit);
        }
        var rename = new MenuFlyoutItem { Text = "Rename..." };
        rename.Click += async (_, _) => await RenameAsync(entry.Entry);
        menu.Items.Add(rename);

        var delete = new MenuFlyoutItem { Text = "Delete" };
        delete.Click += async (_, _) => await DeleteAsync(entry.Entry);
        menu.Items.Add(delete);

        menu.ShowAt(FileList, e.GetPosition(FileList));
    }

    private async Task DownloadAsync(SftpEntry entry)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedFileName = entry.Name;
        picker.FileTypeChoices.Add("All files", [Path.GetExtension(entry.Name) is { Length: > 0 } ext ? ext : "."]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenStreamForWriteAsync();
            await _sftp.DownloadAsync(entry.FullPath, stream);
        }
        catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        await UploadFilesAsync(files.Select(f => f.Path));
    }

    private async Task UploadFilesAsync(IEnumerable<string> localPaths)
    {
        foreach (var local in localPaths)
        {
            try
            {
                await using var f = File.OpenRead(local);
                await _sftp.UploadAsync(f, SftpSession.Combine(_path, Path.GetFileName(local)));
            }
            catch (Exception ex) { ErrorBar.Message = $"{Path.GetFileName(local)}: {ex.Message}"; ErrorBar.IsOpen = true; }
        }
        await LoadAsync();
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await UploadFilesAsync(items.OfType<StorageFile>().Select(f => f.Path));
    }

    private async Task RenameAsync(SftpEntry entry)
    {
        var box = new TextBox { Text = entry.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Rename",
            Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        try
        {
            await _sftp.RenameAsync(entry.FullPath, SftpSession.Combine(SftpSession.Parent(entry.FullPath), box.Text.Trim()));
            await LoadAsync();
        }
        catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
    }

    private async Task DeleteAsync(SftpEntry entry)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Delete",
            Content = new TextBlock { Text = $"Delete {entry.Name}? This cannot be undone.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (entry.IsDirectory) await _sftp.DeleteDirectoryAsync(entry.FullPath);
            else await _sftp.DeleteFileAsync(entry.FullPath);
            await LoadAsync();
        }
        catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "New folder name" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "New folder",
            Content = box, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        try
        {
            await _sftp.CreateDirectoryAsync(SftpSession.Combine(_path, box.Text.Trim()));
            await LoadAsync();
        }
        catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
    }
}
