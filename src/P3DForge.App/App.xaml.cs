using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using P3DForge.App.Editor;
using P3DForge.App.Shell;

namespace P3DForge.App;

public partial class App : Application
{
    /// <summary>A .p3d passed on the command line ("Open with", drag onto the exe).</summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Modding data uses '.' as decimal separator regardless of the Windows locale.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Report(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Report(a.Exception);
            a.SetObserved();
        };

        StartupFile = e.Args.FirstOrDefault(a => a.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(a));
        AppServices.Initialize();
        FeatureLoader.InstallAll();
        base.OnStartup(e);
        // Automated test runs (engineers verifying features) must not steal the keyboard focus from the user.
        new MainWindow { ShowActivated = Environment.GetEnvironmentVariable("P3DFORGE_AUTOMATION") is null }.Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = true; // keep the editor alive; the user can still save
    }

    static void Report(Exception? ex)
    {
        if (ex is null) return;
        System.Diagnostics.Debug.WriteLine(ex);
        try
        {
            Current?.Dispatcher.BeginInvoke(() => AppServices.Editor.Log($"Unexpected error: {ex.Message}", LogLevel.Error));
        }
        catch
        {
            // ignore
        }
    }
}
