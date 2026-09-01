using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using RxV4A.Core;
using RxV4A.Host;

namespace RxV4A.Desktop;

public partial class MainWindow : Window
{
    private readonly IDeviceManager _deviceManager;
    private readonly IControlOrchestrator _orchestrator;
    private readonly IRegisteredActionService _registeredActions;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly GlobalHotkeyService _globalHotkeys;
    private HwndSource? _windowSource;
    private bool _allowClose;
    private bool _pingConfirmationPromptActive;
    private string? _declinedPingConfirmationSignature;

    public MainWindow(
        IDeviceManager deviceManager,
        IControlOrchestrator orchestrator,
        IRegisteredActionService registeredActions,
        AppSettings settings,
        ISettingsStore settingsStore)
    {
        _deviceManager = deviceManager;
        _orchestrator = orchestrator;
        _registeredActions = registeredActions;
        _settings = settings;
        _settingsStore = settingsStore;
        _globalHotkeys = new GlobalHotkeyService(settings, ExecuteHotkeyActionAsync);
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(windowHandle);
            _windowSource?.AddHook(MainWindowMessageHook);
            _globalHotkeys.RegistrationsChanged += GlobalHotkeys_RegistrationsChanged;
            _globalHotkeys.Initialize(windowHandle);
            UpdateHotkeyStatus(_globalHotkeys.Registrations);
        };
        ManualHostTextBox.Text = deviceManager.Settings.ManualHost ?? string.Empty;
        var interfaceOptions = new List<DiscoveryInterfaceOption>
        {
            new("自動選択（すべての有効なNIC）", null)
        };
        interfaceOptions.AddRange(deviceManager.GetDiscoveryNetworkInterfaces().Select(item =>
            new DiscoveryInterfaceOption(
                $"{item.Name}  /{item.PrefixLength}" +
                (item.HasDefaultGateway ? "  （既定経路あり）" : string.Empty),
                item.Id)));
        DiscoveryNetworkInterfaceComboBox.ItemsSource = interfaceOptions;
        DiscoveryNetworkInterfaceComboBox.SelectedItem = interfaceOptions.FirstOrDefault(item =>
            string.Equals(item.Id, deviceManager.Settings.DiscoveryNetworkInterfaceId,
                StringComparison.OrdinalIgnoreCase)) ?? interfaceOptions[0];
        _deviceManager.SnapshotChanged += DeviceManager_SnapshotChanged;
        _orchestrator.StateChanged += Orchestrator_StateChanged;
        Loaded += (_, _) => MaybePromptForBroadPingScan(_deviceManager.Snapshot);
        UpdateSnapshot(_deviceManager.Snapshot);
        UpdateControlState();
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void StartMinimizedToTray()
    {
        ShowInTaskbar = false;
        _ = new WindowInteropHelper(this).EnsureHandle();
    }

    public void AllowClose() => _allowClose = true;

    public event EventHandler? MinimizedToTray;

    public async Task ExecutePowerAsync(MainPower power)
    {
        await RunControlOperationAsync(() => _orchestrator.SetPowerAsync(power, allowBlockedOn: false));
    }

    private async void PowerOnButton_Click(object sender, RoutedEventArgs e) =>
        await ExecutePowerAsync(MainPower.On);

    private async void StandbyButton_Click(object sender, RoutedEventArgs e) =>
        await ExecutePowerAsync(MainPower.Standby);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _declinedPingConfirmationSignature = null;
        await RunUiOperationAsync(() => _deviceManager.RefreshAsync());
        MaybePromptForBroadPingScan(_deviceManager.Snapshot);
    }

    private async void SaveHostButton_Click(object sender, RoutedEventArgs e)
    {
        var interfaceId = (DiscoveryNetworkInterfaceComboBox.SelectedItem as DiscoveryInterfaceOption)?.Id;
        _declinedPingConfirmationSignature = null;
        await RunUiOperationAsync(() =>
            _deviceManager.UpdateConnectionSettingsAsync(ManualHostTextBox.Text, interfaceId));
        MaybePromptForBroadPingScan(_deviceManager.Snapshot);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _globalHotkeys.Dispose();
            _windowSource?.RemoveHook(MainWindowMessageHook);
            _deviceManager.SnapshotChanged -= DeviceManager_SnapshotChanged;
            _orchestrator.StateChanged -= Orchestrator_StateChanged;
            return;
        }

        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        MinimizeToTray();
    }

    private IntPtr MainWindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int wmSystemCommand = 0x0112;
        const int systemCommandMask = 0xFFF0;
        const int systemCommandMinimize = 0xF020;
        if (message == wmSystemCommand &&
            (wParam.ToInt64() & systemCommandMask) == systemCommandMinimize)
        {
            handled = true;
            Dispatcher.BeginInvoke(MinimizeToTray);
        }

        return IntPtr.Zero;
    }

    private void MinimizeToTray()
    {
        ShowInTaskbar = false;
        Hide();
        MinimizedToTray?.Invoke(this, EventArgs.Empty);
    }

    private void DeviceManager_SnapshotChanged(object? sender, DeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => UpdateSnapshot(snapshot));

    private void Orchestrator_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateControlState);

    private void UpdateSnapshot(DeviceSnapshot snapshot)
    {
        ConnectionText.Text = snapshot.ConnectionState switch
        {
            DeviceConnectionState.Connected => "接続中",
            DeviceConnectionState.Connecting => "接続しています…",
            DeviceConnectionState.Reconnecting => "再接続しています…",
            DeviceConnectionState.Unsupported => "対象外の機器",
            _ when snapshot.ErrorCode == "ping_scan_confirmation_required" => "ping探索の確認待ち",
            _ when snapshot.ErrorCode == "ping_scan_too_large" => "サブネットが広すぎます",
            _ => "未接続"
        };
        DeviceText.Text = snapshot.Capabilities is null
            ? snapshot.ErrorCode switch
            {
                "ping_scan_confirmation_required" => "広いサブネットのping探索には確認が必要です",
                "ping_scan_too_large" => "サブネットが/16未満です。接続先を手動指定してください",
                "device_not_found" => "SSDPおよびping探索で見つかりません",
                _ => "—"
            }
            : $"{snapshot.Capabilities.DeviceInfo.ModelName}  (API {snapshot.Capabilities.DeviceInfo.ApiVersion})";
        PowerText.Text = snapshot.MainZone?.Power ?? "—";
        PlaybackText.Text = snapshot.MainZone is null
            ? "—"
            : $"{snapshot.MainZone.Input ?? "—"} / {FormatVolume(snapshot.MainZone.Volume)}";
        UpdatedText.Text = snapshot.UpdatedAt == DateTimeOffset.MinValue
            ? "—"
            : snapshot.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture);

        var canOperate = snapshot.ConnectionState == DeviceConnectionState.Connected &&
                         snapshot.Capabilities?.SupportsZoneFunction("main", "power") == true;
        PowerOnButton.IsEnabled = canOperate;
        StandbyButton.IsEnabled = canOperate;
        UpdateControlState();
        MaybePromptForBroadPingScan(snapshot);
    }

    private async void MaybePromptForBroadPingScan(DeviceSnapshot snapshot)
    {
        var confirmation = snapshot.PingScanConfirmation;
        if (!IsLoaded ||
            snapshot.ErrorCode != "ping_scan_confirmation_required" ||
            confirmation is null ||
            _pingConfirmationPromptActive)
        {
            return;
        }

        var signature = $"{confirmation.BroadestPrefixLength}:{confirmation.HostCount}";
        if (signature == _declinedPingConfirmationSignature)
        {
            return;
        }

        _pingConfirmationPromptActive = true;
        try
        {
            var response = System.Windows.MessageBox.Show(
                $"SSDPとプレフィックス/24以上の自動ping探索ではRX-V4Aを検出できませんでした。\n\n" +
                $"プレフィックス /{confirmation.BroadestPrefixLength} を含む広いサブネットへ、最大 {confirmation.HostCount:N0} ホストのping探索を実行しますか？\n\n" +
                "応答ホストには読み取り専用のgetDeviceInfoを送信します。アンプ設定は変更しません。",
                "広いサブネットのping探索",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (response == MessageBoxResult.Yes)
            {
                _declinedPingConfirmationSignature = null;
                await RunUiOperationAsync(() => _deviceManager.ApproveBroadSubnetPingScanAsync());
            }
            else
            {
                _declinedPingConfirmationSignature = signature;
            }
        }
        finally
        {
            _pingConfirmationPromptActive = false;
        }
    }

    public async Task ExecuteActivityAsync(string activityId) =>
        await RunControlOperationAsync(() => _orchestrator.ActivateActivityAsync(activityId));

    public async Task ToggleContextAsync(string contextId)
    {
        var context = _orchestrator.GetContexts().Single(item =>
            string.Equals(item.Id, contextId, StringComparison.OrdinalIgnoreCase));
        await RunControlOperationAsync(() => context.Active
            ? _orchestrator.DeactivateContextAsync(contextId)
            : _orchestrator.ActivateContextAsync(contextId));
    }

    private async Task ExecuteHotkeyActionAsync(string actionId)
    {
        if (string.Equals(actionId, HotkeyActionIds.PowerOn, StringComparison.OrdinalIgnoreCase))
        {
            await RunControlOperationAsync(() => _orchestrator.SetPowerAsync(MainPower.On, false));
        }
        else if (string.Equals(actionId, HotkeyActionIds.PowerStandby, StringComparison.OrdinalIgnoreCase))
        {
            await RunControlOperationAsync(() => _orchestrator.SetPowerAsync(MainPower.Standby, false));
        }
        else if (actionId.StartsWith("activity:", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteActivityAsync(actionId[9..]);
        }
        else if (actionId.StartsWith("blocker:", StringComparison.OrdinalIgnoreCase))
        {
            await ToggleContextAsync(actionId[8..]);
        }
        else if (actionId.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _registeredActions.ExecuteAsync(actionId[7..]);
            if (!result.Succeeded)
            {
                System.Windows.MessageBox.Show(result.Message, "RX-V4A Manager", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void GlobalHotkeys_RegistrationsChanged(
        object? sender,
        IReadOnlyList<GlobalHotkeyRegistration> registrations) => UpdateHotkeyStatus(registrations);

    private void UpdateHotkeyStatus(IReadOnlyList<GlobalHotkeyRegistration> registrations)
    {
        var failures = registrations.Where(item => !item.Registered).ToArray();
        HotkeyStatusText.Text = failures.Length == 0
            ? $"グローバルホットキー: {registrations.Count}件を登録済み (MOD_NOREPEAT)"
            : $"グローバルホットキー: {registrations.Count - failures.Length}件登録、競合 {failures.Length}件 ({string.Join(", ", failures.Select(item => item.Gesture))})";
        HotkeyStatusText.Foreground = failures.Length == 0
            ? System.Windows.Media.Brushes.DarkGreen
            : System.Windows.Media.Brushes.DarkOrange;
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(
            _settings,
            _settingsStore,
            _globalHotkeys)
        {
            Owner = this
        };
        window.ShowDialog();
        UpdateHotkeyStatus(_globalHotkeys.Registrations);
    }

    private void UpdateControlState()
    {
        var contexts = _orchestrator.GetContexts();
        var active = contexts.Where(item => item.Active).Select(item => item.Id).ToArray();
        var mismatch = contexts.Any(item => item.PowerMismatchWarning);
        BlockerWarningText.Visibility = active.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        BlockerWarningText.Text = active.Length == 0
            ? string.Empty
            : mismatch
                ? $"電源ON禁止中 ({string.Join(", ", active)}) ですが、アンプはONです。自動OFFは行いません。"
                : $"電源ON禁止中: {string.Join(", ", active)}";
    }

    private async Task RunControlOperationAsync(Func<Task<ControlOperationResult>> operation)
    {
        try
        {
            var result = await operation();
            UpdateControlState();
            if (result.Outcome is ControlOutcome.Blocked or ControlOutcome.PartialFailure or ControlOutcome.Failed)
            {
                System.Windows.MessageBox.Show(
                    result.Message + (result.ActiveBlockers.Count > 0
                        ? $"\n有効なコンテキスト: {string.Join(", ", result.ActiveBlockers)}"
                        : string.Empty),
                    "RX-V4A Manager",
                    MessageBoxButton.OK,
                    result.Outcome == ControlOutcome.Blocked ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(
                "操作を完了できませんでした。ログを確認してください。",
                "RX-V4A Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task RunUiOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception switch
                {
                    DeviceUnavailableException => "RX-V4Aに接続されていません。",
                    CapabilityNotSupportedException => "実機のCapabilityにこの操作がありません。",
                    OperationCanceledException => "操作がタイムアウトしました。",
                    ArgumentException => "接続先はIPアドレスまたはホスト名だけを指定してください。",
                    _ => "操作を完了できませんでした。ログを確認してください。"
                },
                "RX-V4A Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string FormatVolume(decimal? volume) =>
        volume.HasValue ? $"{volume.Value:0.0} dB" : "—";

    private sealed record DiscoveryInterfaceOption(string Label, string? Id);
}
