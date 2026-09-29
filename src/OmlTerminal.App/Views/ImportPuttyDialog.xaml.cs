using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.App.Views;

/// <summary>One row in the import list. A plain mutable class (not INotifyPropertyChanged) is enough since
/// IsSelected is only ever read back after the dialog closes, never re-bound while open.</summary>
public sealed class PuttyImportRow
{
    public required SessionProfile Profile { get; init; }
    public required bool KeyNeedsConversion { get; init; }
    public bool IsSelected { get; set; } = true;

    public string Subtitle
    {
        get
        {
            var s = Profile.Protocol == ProtocolKind.Serial
                ? $"{Profile.SerialPortName} @ {Profile.BaudRate} baud"
                : $"{Profile.Username}@{Profile.Host}:{Profile.Port}";
            return KeyNeedsConversion ? $"{s} - key needs OpenSSH format, imported with password auth" : s;
        }
    }
}

public sealed partial class ImportPuttyDialog : ContentDialog
{
    private readonly List<PuttyImportRow> _rows;

    /// <summary>Populated only after a successful "Import Selected" click.</summary>
    public List<SessionProfile> Result { get; } = new();

    public ImportPuttyDialog(XamlRoot xamlRoot, IReadOnlyList<PuttySessionImporter.ImportedSession> found)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;

        _rows = found.Select(f => new PuttyImportRow { Profile = f.Profile, KeyNeedsConversion = f.KeyNeedsConversion }).ToList();
        RowsList.ItemsSource = _rows;
        if (_rows.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            RowsList.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
        }

        PrimaryButtonClick += (_, _) => Result.AddRange(_rows.Where(r => r.IsSelected).Select(r => r.Profile));
    }
}
