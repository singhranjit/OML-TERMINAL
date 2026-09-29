using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmlTerminal.App.ViewModels;

namespace OmlTerminal.App.Views;

public sealed partial class UnlockDialog : ContentDialog
{
    public UnlockDialog(XamlRoot xamlRoot, MainViewModel vm)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ElementTheme.Dark;
        Opened += (_, _) => PasswordBox.Focus(FocusState.Programmatic);
        PrimaryButtonClick += (_, args) =>
        {
            if (vm.TryUnlock(PasswordBox.Password)) return;
            args.Cancel = true; // stay open and let them retry
            ErrorBar.IsOpen = true;
            PasswordBox.Password = "";
            PasswordBox.Focus(FocusState.Programmatic);
        };
    }
}
