using Microsoft.UI.Xaml;

namespace Orchestration.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogStartupError(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception e)
        {
            LogStartupError(e);
            throw;
        }
    }

    private static void LogStartupError(Exception exception)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tether");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "startup-error.log"), exception.ToString());
    }
}
