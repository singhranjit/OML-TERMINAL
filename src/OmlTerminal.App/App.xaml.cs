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
