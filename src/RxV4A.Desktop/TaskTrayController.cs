using System.Drawing;
using RxV4A.Core;
using RxV4A.Host;
using Forms = System.Windows.Forms;

namespace RxV4A.Desktop;

public sealed class TaskTrayController : IDisposable
{
    private readonly IDeviceManager _deviceManager;
    private readonly IControlOrchestrator _orchestrator;
    private readonly MainWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly CompactPowerWindow _compactPowerWindow;
    private readonly Icon? _applicationIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _statusItem;

    public TaskTrayController(
        IDeviceManager deviceManager,
        IControlOrchestrator orchestrator,
        MainWindow mainWindow,
        SettingsWindow settingsWindow,
        CompactPowerWindow compactPowerWindow,
        Action requestExit)
    {
        _deviceManager = deviceManager;
        _orchestrator = orchestrator;
        _mainWindow = mainWindow;
        _settingsWindow = settingsWindow;
        _compactPowerWindow = compactPowerWindow;
        _statusItem = new Forms.ToolStripMenuItem("未接続") { Enabled = false };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("ミニ電源操作", null, (_, _) => _compactPowerWindow.ShowFromTray());
        menu.Items.Add("操作画面を開く", null, (_, _) => ShowMainWindow());
        menu.Items.Add("設定画面を開く", null, (_, _) => _settingsWindow.ShowFromTray());
        menu.Items.Add("状態を更新", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("アンプ ON", null, async (_, _) => await _mainWindow.ExecutePowerAsync(MainPower.On));
        menu.Items.Add("Standby", null, async (_, _) => await _mainWindow.ExecutePowerAsync(MainPower.Standby));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => requestExit());

        _applicationIcon = string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? null
            : Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? SystemIcons.Application,
            Text = "NAVA - 未接続",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };
        _deviceManager.SnapshotChanged += DeviceManager_SnapshotChanged;
        _orchestrator.StateChanged += Orchestrator_StateChanged;
        Update(_deviceManager.Snapshot);
    }

    public void Dispose()
    {
        _deviceManager.SnapshotChanged -= DeviceManager_SnapshotChanged;
        _orchestrator.StateChanged -= Orchestrator_StateChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon?.Dispose();
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _deviceManager.RefreshAsync();
        }
        catch
        {
            // The shared snapshot and structured log expose the failure state.
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow.Dispatcher.CheckAccess())
        {
            _mainWindow.ShowFromTray();
        }
        else
        {
            _mainWindow.Dispatcher.BeginInvoke(_mainWindow.ShowFromTray);
        }
    }

    private void DeviceManager_SnapshotChanged(object? sender, DeviceSnapshot snapshot)
    {
        if (_mainWindow.Dispatcher.CheckAccess())
        {
            Update(snapshot);
        }
        else
        {
            _mainWindow.Dispatcher.BeginInvoke(() => Update(snapshot));
        }
    }

    private void Orchestrator_StateChanged(object? sender, EventArgs e)
    {
        if (_mainWindow.Dispatcher.CheckAccess())
        {
            Update(_deviceManager.Snapshot);
        }
        else
        {
            _mainWindow.Dispatcher.BeginInvoke(() => Update(_deviceManager.Snapshot));
        }
    }

    private void Update(DeviceSnapshot snapshot)
    {
        var state = snapshot.ConnectionState == DeviceConnectionState.Connected
            ? snapshot.MainZone?.Power ?? "接続中"
            : "未接続";
        var contexts = _orchestrator.GetContexts();
        var blockers = contexts.Where(item => item.Active).Select(item => item.Id).ToArray();
        var mismatch = contexts.Any(item => item.PowerMismatchWarning);
        _statusItem.Text = blockers.Length == 0
            ? $"状態: {state}"
            : $"状態: {state} / ブロック: {string.Join(",", blockers)}";
        var suffix = blockers.Length == 0 ? state : mismatch ? "禁止中・外部ON" : "電源ON禁止中";
        _notifyIcon.Text = $"NAVA - {suffix}";
    }
}
