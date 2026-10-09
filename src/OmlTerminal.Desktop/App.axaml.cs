using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OmlTerminal.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // An exception in one tool must not take every open session down with it: log it, show it, carry on.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { e.SetObserved(); Report(e.Exception); };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    public static void Report(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Core.Persistence.AppPaths.DataDirectory);
            File.AppendAllText(Path.Combine(Core.Persistence.AppPaths.DataDirectory, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
        Console.Error.WriteLine(ex);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => MainWindow.Current?.ShowError(ex));
    }
}
