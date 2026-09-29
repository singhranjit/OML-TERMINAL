using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.App.Views;

public sealed class CredentialImportRow
{
    public required Credential Credential { get; init; }
    public bool IsSelected { get; set; } = true;
    public string MatchText { get; init; } = "";
}

/// <summary>Parses a MobaXterm-style "Name(username) = password" credential list and imports the picked ones
/// into the vault (see <see cref="CredentialListImporter"/>). Each line is its own vault entry - never merged
/// by username, since the same login name can carry different passwords under different credential names.</summary>
public sealed partial class ImportCredentialsDialog : ContentDialog
{
    private readonly IReadOnlyList<SessionProfile> _existingSessions;
    private readonly ObservableCollection<CredentialImportRow> _rows = new();

    public List<Credential> Result { get; } = new();

    public ImportCredentialsDialog(XamlRoot xamlRoot, IReadOnlyList<SessionProfile> existingSessions)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        _existingSessions = existingSessions;
        RowsList.ItemsSource = _rows;
        UpdateSummary();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _rows.Clear();
        foreach (var credential in CredentialListImporter.Parse(InputBox.Text))
        {
            var matches = _existingSessions.Count(s => string.Equals(s.Username, credential.Username, StringComparison.OrdinalIgnoreCase) && s.CredentialId is null);
            _rows.Add(new CredentialImportRow
            {
                Credential = credential,
                MatchText = matches > 0 ? $"→ {matches} session{(matches == 1 ? "" : "s")}" : "",
            });
        }
        UpdateSummary();
    }

    private void UpdateSummary() =>
        SummaryText.Text = _rows.Count == 0 ? "Paste a credential list above, or load one from a file." : $"Parsed {_rows.Count} credential(s).";

    private async void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try { InputBox.Text = await File.ReadAllTextAsync(file.Path); }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Credential).ToList();
        if (selected.Count == 0) { ErrorText.Text = "Select at least one credential to import."; args.Cancel = true; return; }
        Result.AddRange(selected);
    }
}
