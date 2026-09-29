using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Models;

namespace OmlTerminal.App.Views;

public sealed class ImportSessionRow
{
    public required SessionProfile Profile { get; init; }
    public bool IsSelected { get; set; } = true;

    public string Subtitle => Profile.Protocol == ProtocolKind.Serial
        ? $"{Profile.SerialPortName} @ {Profile.BaudRate} baud"
        : $"{Profile.Username}@{Profile.Host}:{Profile.Port}";
}

/// <summary>Generic "here's what we found, pick which ones to bring in" checklist - used for CSV/JSON bulk
/// import. Deliberately a separate small dialog from ImportPuttyDialog rather than a shared abstraction: the
/// two have slightly different row concerns (PuTTY's .ppk conversion note has no CSV/JSON equivalent) and are
/// each simple enough that sharing isn't worth the indirection.</summary>
public sealed partial class ImportSessionsDialog : ContentDialog
{
    private readonly List<ImportSessionRow> _rows;

    public List<SessionProfile> Result { get; } = new();

    public ImportSessionsDialog(XamlRoot xamlRoot, IReadOnlyList<SessionProfile> found, string sourceDescription)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;

        SummaryText.Text = found.Count == 0
            ? $"No valid sessions were found in {sourceDescription}."
            : $"Found {found.Count} session(s) in {sourceDescription}. Select the ones to import.";

        _rows = found.Select(p => new ImportSessionRow { Profile = p }).ToList();
        RowsList.ItemsSource = _rows;
        if (_rows.Count == 0)
        {
            RowsList.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
        }

        PrimaryButtonClick += (_, _) => Result.AddRange(_rows.Where(r => r.IsSelected).Select(r => r.Profile));
    }
}
