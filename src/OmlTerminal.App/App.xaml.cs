using Microsoft.UI.Xaml;

namespace OmlTerminal.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Core.Persistence.AppPaths.DataDirectory;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
            }
            catch { }
            // A failing button handler shouldn't take every open session down with it: keep running once the
            // window is up (startup failures still end the app - there'd be nothing usable to keep).
            if (MainWindow is not null)
            {
                e.Handled = true;
                MainWindow.ReportError(e.Exception);
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();

        var link = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith(Core.Models.OmlNodeLink.Scheme + "://", StringComparison.OrdinalIgnoreCase));
        if (link is not null)
            MainWindow.DispatcherQueue.TryEnqueue(async () => await MainWindow.OmlNodeAsync(link));
    }
}
