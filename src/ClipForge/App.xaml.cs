using ClipForge.Services;
using System.Windows.Threading;
using Velopack;

namespace ClipForge;

public partial class App : System.Windows.Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        ProcessSecurityService.Apply();
        // Applying an update is always initiated explicitly from ClipForge's update panel.
        // This prevents a previously staged package from being applied implicitly at startup.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistrationService.TryRemoveForUninstall())
            .Run();

        var launchOptions = AppLaunchOptions.Parse(args);

        using var singleInstance = SingleInstanceService.Acquire();
        if (!singleInstance.IsPrimary)
        {
            if (launchOptions.ShouldActivateExistingInstance && !singleInstance.TrySignalPrimary())
            {
                System.Windows.MessageBox.Show(
                    "ClipForge is already running. Open it from the system tray, or exit the existing instance before trying again.",
                    "ClipForge is already running",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }

            return;
        }

        if (SingleInstanceService.HasLegacyClipForgeProcessInCurrentSession())
        {
            if (launchOptions.ShouldActivateExistingInstance)
            {
                System.Windows.MessageBox.Show(
                    "Another ClipForge process from an older build is already running. Exit every existing ClipForge instance from the system tray once, then start ClipForge again.",
                    "Close the older ClipForge instance",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }

            return;
        }

        var app = new App();
        app.InitializeComponent();
        var mainWindow = new MainWindow(launchOptions);
        app.MainWindow = mainWindow;
        singleInstance.ActivationRequested += (_, _) =>
            QueuePrimaryWindowActivation(app, attemptsRemaining: 5);
        singleInstance.StartListening();
        mainWindow.ShowForLaunch();
        app.Run();
    }

    private static void QueuePrimaryWindowActivation(App app, int attemptsRemaining)
    {
        if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var mainWindow = app.Windows.OfType<MainWindow>().FirstOrDefault();
            if (mainWindow is not null)
            {
                mainWindow.ShowFromSecondaryInstance();
            }
            else if (attemptsRemaining > 0)
            {
                QueuePrimaryWindowActivation(app, attemptsRemaining - 1);
            }
        });
    }
}
