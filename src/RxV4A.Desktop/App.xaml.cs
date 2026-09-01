using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RxV4A.Host;

namespace RxV4A.Desktop;

public partial class App : System.Windows.Application
{
    private WebApplication? _host;
    private TaskTrayController? _taskTray;
    private MainWindow? _mainWindow;
    private CompactPowerWindow? _compactPowerWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = await LocalApiApplication.BuildAsync(e.Args);
            await _host.StartAsync();

            var manager = _host.Services.GetRequiredService<IDeviceManager>();
            var orchestrator = _host.Services.GetRequiredService<IControlOrchestrator>();
            var settings = _host.Services.GetRequiredService<AppSettings>();
            var settingsStore = _host.Services.GetRequiredService<ISettingsStore>();
            var registeredActions = _host.Services.GetRequiredService<IRegisteredActionService>();
            _mainWindow = new MainWindow(manager, orchestrator, registeredActions, settings, settingsStore);
            _compactPowerWindow = new CompactPowerWindow(
                manager,
                orchestrator,
                settings,
                settingsStore);
            MainWindow = _mainWindow;
            _mainWindow.MinimizedToTray += (_, _) => _compactPowerWindow.ShowFromTray();
            _taskTray = new TaskTrayController(
                manager,
                orchestrator,
                _mainWindow,
                _compactPowerWindow,
                RequestExit);
            _mainWindow.StartMinimizedToTray();
            _compactPowerWindow.Show();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"RX-V4A Managerを開始できませんでした。\n\n{exception.Message}",
                "RX-V4A Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskTray?.Dispose();
        if (_host is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _host.StopAsync(timeout.Token).GetAwaiter().GetResult();
                _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Process shutdown must continue even if a hosted service is already unavailable.
            }
        }

        base.OnExit(e);
    }

    private void RequestExit()
    {
        _mainWindow?.AllowClose();
        _compactPowerWindow?.AllowClose();
        Shutdown();
    }
}
