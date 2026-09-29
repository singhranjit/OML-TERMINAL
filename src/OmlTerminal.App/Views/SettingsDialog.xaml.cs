using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Models;

namespace OmlTerminal.App.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings _settings;

    /// <summary>A validated new master password to apply, or null if unchanged.</summary>
    public string? NewMasterPassword { get; private set; }
    public bool RemoveMasterPassword { get; private set; }

    public SettingsDialog(XamlRoot xamlRoot, AppSettings settings)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        _settings = settings;
        FontBox.Text = settings.FontFamily;
        SizeBox.Value = settings.FontSize;
        ScrollbackBox.Value = settings.ScrollbackLines;
        PlinkBox.Text = settings.PlinkPath ?? "";
        LogDirBox.Text = settings.LogDirectory;
        AlwaysLogBox.IsChecked = settings.AlwaysLogSessions;
        AutoReconnectBox.IsChecked = settings.AutoReconnect;
        RestoreWorkspaceBox.IsChecked = settings.RestoreWorkspaceOnLaunch;
        XServerPathBox.Text = settings.XServerPath;
        XDisplayBox.Value = settings.XDisplay;
        AutoStartXBox.IsChecked = settings.StartXServerOnLaunch;
        var found = Core.Shells.XServerManager.DetectInstalled();
        XServerHint.Text = found.Count > 0
            ? $"Detected: {string.Join(", ", found.Select(f => f.Name))}. X11 sessions use the OpenSSH engine."
            : "No X server detected. Install VcXsrv, Xming or Cygwin/X (xorg-server) to show remote GUI apps.";

        MasterHint.Text = settings.HasMasterPassword
            ? "A master password is set. Saved passwords are encrypted. Enter a new one to change it."
            : "Encrypts saved passwords with AES-256. You will be asked for it each time the app starts. There is no recovery if you forget it.";
        RemoveMasterBox.Visibility = settings.HasMasterPassword ? Visibility.Visible : Visibility.Collapsed;

        PrimaryButtonClick += OnSave;
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ErrorBar.IsOpen = false;
        string? newMaster = null;
        bool remove = RemoveMasterBox.IsChecked == true;

        if (!remove && (NewMasterBox.Password.Length > 0 || ConfirmMasterBox.Password.Length > 0))
        {
            if (NewMasterBox.Password != ConfirmMasterBox.Password) { Fail(args, "The two master passwords do not match."); return; }
            if (NewMasterBox.Password.Length < 6) { Fail(args, "Use at least 6 characters."); return; }
            newMaster = NewMasterBox.Password;
        }

        if (!string.IsNullOrWhiteSpace(FontBox.Text)) _settings.FontFamily = FontBox.Text.Trim();
        if (!double.IsNaN(SizeBox.Value)) _settings.FontSize = SizeBox.Value;
        if (!double.IsNaN(ScrollbackBox.Value)) _settings.ScrollbackLines = (int)ScrollbackBox.Value;
        _settings.PlinkPath = string.IsNullOrWhiteSpace(PlinkBox.Text) ? null : PlinkBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(LogDirBox.Text)) _settings.LogDirectory = LogDirBox.Text.Trim();
        _settings.AlwaysLogSessions = AlwaysLogBox.IsChecked == true;
        _settings.AutoReconnect = AutoReconnectBox.IsChecked == true;
        _settings.RestoreWorkspaceOnLaunch = RestoreWorkspaceBox.IsChecked == true;
        _settings.XServerPath = XServerPathBox.Text.Trim();
        if (!double.IsNaN(XDisplayBox.Value)) _settings.XDisplay = (int)XDisplayBox.Value;
        _settings.StartXServerOnLaunch = AutoStartXBox.IsChecked == true;
        NewMasterPassword = newMaster;
        RemoveMasterPassword = remove;
    }

    private async void BrowseLogDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) LogDirBox.Text = folder.Path;
    }

    private void Fail(ContentDialogButtonClickEventArgs args, string message)
    {
        args.Cancel = true;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
