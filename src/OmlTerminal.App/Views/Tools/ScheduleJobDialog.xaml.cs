using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;

namespace OmlTerminal.App.Views.Tools;

public sealed partial class ScheduleJobDialog : ContentDialog
{
    private const string CustomLabel = "Custom commands...";
    private readonly Guid? _existingId;
    private readonly DateTime? _existingLastRunUtc;

    /// <summary>Set once the Primary button click validates successfully.</summary>
    public ScheduledBackupJob? Result { get; private set; }

    public ScheduleJobDialog(XamlRoot xamlRoot, IReadOnlyList<SessionProfile> devices, ScheduledBackupJob? existing)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        Title = existing is null ? "New scheduled backup" : "Edit scheduled backup";

        DeviceList.ItemsSource = devices;
        foreach (var p in ConfigBackup.Presets) PresetBox.Items.Add(p);
        PresetBox.Items.Add(CustomLabel);

        if (existing is { } job)
        {
            _existingId = job.Id;
            _existingLastRunUtc = job.LastRunUtc;
            NameBox.Text = job.Name;
            IntervalBox.Value = job.IntervalMinutes;
            EnabledBox.IsChecked = job.Enabled;
            RootDirectoryBox.Text = job.RootDirectory;
            CustomMode.SelectedIndex = job.CustomMode == BackupMode.Exec ? 0 : 1;
            if (job.CustomCommandsText.Length > 0) CustomCommands.Text = job.CustomCommandsText;
            var preset = ConfigBackup.Presets.FirstOrDefault(p => p.Name == job.PresetName);
            PresetBox.SelectedItem = preset is not null ? (object)preset : CustomLabel;
            foreach (var d in devices.Where(d => job.DeviceIds.Contains(d.Id))) DeviceList.SelectedItems.Add(d);
        }
        else
        {
            NameBox.Text = "Scheduled backup";
            if (PresetBox.Items.Count > 0) PresetBox.SelectedIndex = 0;
        }
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = PresetBox.SelectedItem as string == CustomLabel;
        CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        if (PresetBox.SelectedItem is BackupPreset p)
            PresetHint.Text = $"{(p.Mode == BackupMode.Exec ? "exec" : "shell")}:  {string.Join("  →  ", p.Commands)}";
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (await ToolUi.PickFolderAsync() is { } path) RootDirectoryBox.Text = path;
    }

    private void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = NameBox.Text.Trim();
        var selected = DeviceList.SelectedItems.OfType<SessionProfile>().Select(d => d.Id).ToList();
        bool custom = PresetBox.SelectedItem as string == CustomLabel;

        if (name.Length == 0) { ErrorText.Text = "Name is required."; args.Cancel = true; return; }
        if (selected.Count == 0) { ErrorText.Text = "Pick at least one device."; args.Cancel = true; return; }
        if (custom && ConfigBackup.Custom(BackupMode.Exec, CustomCommands.Text).Commands.Count == 0)
        { ErrorText.Text = "Enter at least one command."; args.Cancel = true; return; }
        if (!custom && PresetBox.SelectedItem is not BackupPreset) { ErrorText.Text = "Choose a vendor / command set."; args.Cancel = true; return; }
        if (double.IsNaN(IntervalBox.Value) || IntervalBox.Value < 1) { ErrorText.Text = "Interval must be at least 1 minute."; args.Cancel = true; return; }

        Result = new ScheduledBackupJob
        {
            Id = _existingId ?? Guid.NewGuid(),
            LastRunUtc = _existingLastRunUtc,
            Name = name,
            DeviceIds = selected,
            PresetName = custom ? "Custom" : ((BackupPreset)PresetBox.SelectedItem).Name,
            CustomMode = CustomMode.SelectedIndex == 0 ? BackupMode.Exec : BackupMode.Shell,
            CustomCommandsText = CustomCommands.Text,
            IntervalMinutes = (int)IntervalBox.Value,
            Enabled = EnabledBox.IsChecked == true,
            RootDirectory = RootDirectoryBox.Text.Trim(),
        };
    }
}
