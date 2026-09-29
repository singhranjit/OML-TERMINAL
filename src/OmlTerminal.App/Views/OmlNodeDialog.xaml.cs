using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace OmlTerminal.App.Views;

public sealed partial class OmlNodeDialog : ContentDialog
{
    /// <summary>Non-null only when the user confirmed a single-node link that passed validation.</summary>
    public SessionProfile? Result { get; private set; }
    public string? Source { get; private set; }

    /// <summary>Non-null instead of Result when the pasted link is a whole-lab launch link.</summary>
    public OmlLabLink? LabLink { get; private set; }

    private SessionProfile? _valid;
    private OmlLabLink? _validLab;

    public OmlNodeDialog(XamlRoot xamlRoot, string? initialLink = null)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        PrimaryButtonClick += (_, _) => { Result = _valid; LabLink = _validLab; };
        SecondaryButtonClick += (_, _) => Result = _valid;
        if (!string.IsNullOrWhiteSpace(initialLink)) LinkBox.Text = initialLink;
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text)) LinkBox.Text = (await view.GetTextAsync()).Trim();
        }
        catch { }
    }

    private void LinkBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _valid = null;
        _validLab = null;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = false;
        ErrorBar.IsOpen = false;
        SummaryCard.Visibility = Visibility.Collapsed;
        PrimaryButtonText = "Connect";
        SecondaryButtonText = "Save to sidebar";
        if (string.IsNullOrWhiteSpace(LinkBox.Text)) return;

        // A "lab" link carries a token to fetch the lab's current node list, not connection details directly -
        // try that schema first, since a single-node link will never itself claim kind:"lab".
        var lab = OmlLabLinkParser.Parse(LinkBox.Text);
        if (lab.Ok)
        {
            _validLab = lab.Link;
            SummaryTitle.Text = lab.Link!.LabName;
            SummaryTarget.Text = $"LAB  {new Uri(lab.Link.OmlHost).Host}";
            SummarySource.Text = $"Requested by: {(string.IsNullOrEmpty(lab.Link.Source) ? "an OML lab" : lab.Link.Source)} - choose a node next.";
            SummaryCard.Visibility = Visibility.Visible;
            PrimaryButtonText = "Browse Nodes";
            SecondaryButtonText = "";
            IsPrimaryButtonEnabled = true;
            IsSecondaryButtonEnabled = false;
            return;
        }

        var r = OmlNodeLink.Parse(LinkBox.Text, DateTimeOffset.UtcNow);
        if (!r.Ok)
        {
            ErrorBar.Message = r.Error;
            ErrorBar.IsOpen = true;
            return;
        }
        _valid = r.Profile;
        Source = r.Source;
        var p = r.Profile!;
        SummaryTitle.Text = p.Name;
        SummaryTarget.Text = $"{p.ProtocolLabel}  {(string.IsNullOrEmpty(p.Username) ? "" : p.Username + "@")}{p.Host}:{p.Port}";
        SummarySource.Text = $"Requested by: {r.Source}";
        SummaryCard.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = true;
    }
}
