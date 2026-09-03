using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using RxV4A.Core;
using RxV4A.Host;

namespace RxV4A.Desktop;

public partial class SettingsWindow : Window
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
    private bool _updatingDailyControls;
    private string? _declinedPingConfirmationSignature;

    public SettingsWindow(
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
            _windowSource?.AddHook(SettingsWindowMessageHook);
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
            _windowSource?.RemoveHook(SettingsWindowMessageHook);
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

    private IntPtr SettingsWindowMessageHook(
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
            : $"{snapshot.MainZone.Input ?? "—"} / {FormatVolume(snapshot.MainZone)}";
        UpdatedText.Text = snapshot.UpdatedAt == DateTimeOffset.MinValue
            ? "—"
            : snapshot.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture);

        var canOperate = snapshot.ConnectionState == DeviceConnectionState.Connected &&
                         snapshot.Capabilities?.SupportsZoneFunction("main", "power") == true;
        PowerOnButton.IsEnabled = canOperate;
        StandbyButton.IsEnabled = canOperate;
        UpdateDailyControls(snapshot);
        UpdateControlState();
        MaybePromptForBroadPingScan(snapshot);
    }

    private void UpdateDailyControls(DeviceSnapshot snapshot)
    {
        _updatingDailyControls = true;
        try
        {
            var connected = snapshot.ConnectionState == DeviceConnectionState.Connected;
            var capabilities = snapshot.Capabilities;
            var zone = capabilities?.FindZone("main");
            var status = snapshot.MainZone;

            var inputs = zone?.Inputs
                .Select(input => input.Id)
                .Where(ApplicationScope.IsOperationalInput)
                .ToArray() ?? [];
            InputPanel.Visibility = inputs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            InputComboBox.ItemsSource = inputs;
            InputComboBox.SelectedItem = inputs.FirstOrDefault(input =>
                string.Equals(input, status?.Input, StringComparison.OrdinalIgnoreCase));
            InputComboBox.IsEnabled = connected;
            SetInputButton.IsEnabled = connected;

            var volumeRange = capabilities?.FindZoneRange("main", "volume");
            var hasVolume = capabilities?.SupportsZoneFunction("main", "volume") == true &&
                            volumeRange?.Minimum is decimal &&
                            volumeRange.Maximum is decimal &&
                            volumeRange.Step is > 0;
            VolumePanel.Visibility = hasVolume ? Visibility.Visible : Visibility.Collapsed;
            if (hasVolume)
            {
                VolumeSlider.Minimum = (double)volumeRange!.Minimum!.Value;
                VolumeSlider.Maximum = (double)volumeRange.Maximum!.Value;
                VolumeSlider.TickFrequency = (double)volumeRange.Step!.Value;
                VolumeSlider.IsEnabled = connected;
                SetVolumeButton.IsEnabled = connected;
                if (status?.Volume is decimal volume)
                {
                    VolumeSlider.Value = Math.Clamp((double)volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
                }

                VolumeValueText.Text = FormatValue((decimal)VolumeSlider.Value);
                VolumeSlider.ToolTip = $"{volumeRange.Minimum} ～ {volumeRange.Maximum} / {volumeRange.Step}刻み";
            }

            SetBooleanControl(MutePanel, MuteCheckBox, capabilities, "mute", status?.Mute, connected);

            var programs = zone?.SoundPrograms.ToArray() ?? [];
            var hasPrograms = capabilities?.SupportsZoneFunction("main", "sound_program") == true &&
                              programs.Length > 0;
            SoundProgramPanel.Visibility = hasPrograms ? Visibility.Visible : Visibility.Collapsed;
            SoundProgramComboBox.ItemsSource = programs;
            SoundProgramComboBox.SelectedItem = programs.FirstOrDefault(program =>
                string.Equals(program, status?.SoundProgram, StringComparison.OrdinalIgnoreCase));
            SoundProgramComboBox.IsEnabled = connected;
            SetSoundProgramButton.IsEnabled = connected;

            SetBooleanControl(null, Surround3dCheckBox, capabilities, "surround_3d", status?.Surround3d, connected);
            SetBooleanControl(null, DirectCheckBox, capabilities, "direct", status?.Direct, connected);
            SetBooleanControl(null, PureDirectCheckBox, capabilities, "pure_direct", status?.PureDirect, connected);
            SetBooleanControl(null, EnhancerCheckBox, capabilities, "enhancer", status?.Enhancer, connected);
            ProcessingPanel.Visibility = ProcessingPanel.Children.OfType<System.Windows.Controls.CheckBox>()
                .Any(control => control.Visibility == Visibility.Visible)
                ? Visibility.Visible
                : Visibility.Collapsed;

            var hasTone = capabilities?.SupportsZoneFunction("main", "tone_control") == true;
            TonePanel.Visibility = hasTone ? Visibility.Visible : Visibility.Collapsed;
            TonePanel.IsEnabled = connected;
            if (hasTone)
            {
                SetModes(ToneModeComboBox, zone!.ToneControlModes, status?.ToneControl?.Mode);
                BassTextBox.Text = FormatValue(status?.ToneControl?.Bass);
                TrebleTextBox.Text = FormatValue(status?.ToneControl?.Treble);
                SetRangeToolTip(BassTextBox, capabilities!.FindZoneRange("main", "tone_control"));
                SetRangeToolTip(TrebleTextBox, capabilities.FindZoneRange("main", "tone_control"));
            }

            var hasEqualizer = capabilities?.SupportsZoneFunction("main", "equalizer") == true;
            EqualizerPanel.Visibility = hasEqualizer ? Visibility.Visible : Visibility.Collapsed;
            EqualizerPanel.IsEnabled = connected;
            if (hasEqualizer)
            {
                SetModes(EqualizerModeComboBox, zone!.EqualizerModes, status?.Equalizer?.Mode);
                EqualizerLowTextBox.Text = FormatValue(status?.Equalizer?.Low);
                EqualizerMidTextBox.Text = FormatValue(status?.Equalizer?.Mid);
                EqualizerHighTextBox.Text = FormatValue(status?.Equalizer?.High);
                var range = capabilities!.FindZoneRange("main", "equalizer");
                SetRangeToolTip(EqualizerLowTextBox, range);
                SetRangeToolTip(EqualizerMidTextBox, range);
                SetRangeToolTip(EqualizerHighTextBox, range);
            }

            var balanceRange = capabilities?.FindZoneRange("main", "balance");
            var hasBalance = capabilities?.SupportsZoneFunction("main", "balance") == true &&
                             balanceRange is not null;
            BalancePanel.Visibility = hasBalance ? Visibility.Visible : Visibility.Collapsed;
            BalancePanel.IsEnabled = connected;
            if (hasBalance)
            {
                BalanceTextBox.Text = FormatValue(status?.Balance);
                SetRangeToolTip(BalanceTextBox, balanceRange);
            }

            CapabilityNoteText.Text = capabilities is null
                ? "接続後、機器が公式APIで広告した操作だけを表示します。"
                : string.Equals(capabilities.DeviceInfo.ModelName, "RX-V4A", StringComparison.OrdinalIgnoreCase)
                    ? "RX-V4A実機確認対象。広告されない機能は表示しません。"
                    : "公式APIとの互換動作です。この機種での実機確認は行っていません。";
        }
        finally
        {
            _updatingDailyControls = false;
        }
    }

    private static void SetBooleanControl(
        FrameworkElement? panel,
        System.Windows.Controls.CheckBox control,
        CapabilitySnapshot? capabilities,
        string capability,
        bool? value,
        bool connected)
    {
        var supported = capabilities?.SupportsZoneFunction("main", capability) == true;
        (panel ?? control).Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        control.IsEnabled = connected;
        control.IsChecked = value;
    }

    private static void SetModes(
        System.Windows.Controls.ComboBox comboBox,
        IReadOnlyList<string> advertisedModes,
        string? currentMode)
    {
        var modes = advertisedModes.Count > 0 ? advertisedModes : ["manual"];
        comboBox.ItemsSource = modes;
        comboBox.SelectedItem = modes.FirstOrDefault(mode =>
            string.Equals(mode, currentMode, StringComparison.OrdinalIgnoreCase)) ?? modes[0];
    }

    private static void SetRangeToolTip(FrameworkElement control, RangeStepFeature? range) =>
        control.ToolTip = range?.Minimum is decimal minimum &&
                          range.Maximum is decimal maximum &&
                          range.Step is decimal step
            ? $"{minimum} ～ {maximum} / {step}刻み"
            : "機器から値域を取得できません";

    private async void SetInputButton_Click(object sender, RoutedEventArgs e)
    {
        if (InputComboBox.SelectedItem is string input)
        {
            await RunUiOperationAsync(() => _deviceManager.SetMainInputAsync(input));
        }
    }

    private async void SetVolumeButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync(() => _deviceManager.SetMainVolumeAsync(GetSnappedVolume()));

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText is not null)
        {
            VolumeValueText.Text = FormatValue((decimal)e.NewValue);
        }
    }

    private async void MuteCheckBox_Click(object sender, RoutedEventArgs e) =>
        await RunBooleanOperationAsync(value => _deviceManager.SetMainMuteAsync(value), MuteCheckBox);

    private async void SetSoundProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (SoundProgramComboBox.SelectedItem is string program)
        {
            await RunUiOperationAsync(() => _deviceManager.SetMainSoundProgramAsync(program));
        }
    }

    private async void Surround3dCheckBox_Click(object sender, RoutedEventArgs e) =>
        await RunBooleanOperationAsync(value => _deviceManager.SetMainSurround3dAsync(value), Surround3dCheckBox);

    private async void DirectCheckBox_Click(object sender, RoutedEventArgs e) =>
        await RunBooleanOperationAsync(value => _deviceManager.SetMainDirectAsync(value), DirectCheckBox);

    private async void PureDirectCheckBox_Click(object sender, RoutedEventArgs e) =>
        await RunBooleanOperationAsync(value => _deviceManager.SetMainPureDirectAsync(value), PureDirectCheckBox);

    private async void EnhancerCheckBox_Click(object sender, RoutedEventArgs e) =>
        await RunBooleanOperationAsync(value => _deviceManager.SetMainEnhancerAsync(value), EnhancerCheckBox);

    private async Task RunBooleanOperationAsync(
        Func<bool, Task<DeviceSnapshot>> operation,
        System.Windows.Controls.CheckBox control)
    {
        if (_updatingDailyControls)
        {
            return;
        }

        await RunUiOperationAsync(() => operation(control.IsChecked == true));
        UpdateDailyControls(_deviceManager.Snapshot);
    }

    private async void SetToneButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync(() => _deviceManager.SetMainToneControlAsync(new ToneControlSettings(
            ToneModeComboBox.SelectedItem as string,
            ParseOptionalDecimal(BassTextBox.Text),
            ParseOptionalDecimal(TrebleTextBox.Text))));

    private async void SetEqualizerButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync(() => _deviceManager.SetMainEqualizerAsync(new EqualizerSettings(
            EqualizerModeComboBox.SelectedItem as string,
            ParseOptionalDecimal(EqualizerLowTextBox.Text),
            ParseOptionalDecimal(EqualizerMidTextBox.Text),
            ParseOptionalDecimal(EqualizerHighTextBox.Text))));

    private async void SetBalanceButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync(() => _deviceManager.SetMainBalanceAsync(
            ParseOptionalDecimal(BalanceTextBox.Text) ??
            throw new ArgumentException("バランス値を入力してください。")));

    private static decimal? ParseOptionalDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var current) ||
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out current))
        {
            return current;
        }

        throw new ArgumentException("数値を入力してください。");
    }

    private static string FormatValue(decimal? value) =>
        value?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;

    private decimal GetSnappedVolume()
    {
        var range = _deviceManager.Snapshot.Capabilities?.FindZoneRange("main", "volume")
            ?? throw new CapabilityNotSupportedException("main.volume.range");
        if (range.Minimum is not decimal minimum || range.Step is not decimal step || step <= 0)
        {
            throw new CapabilityNotSupportedException("main.volume.range");
        }

        var raw = (decimal)VolumeSlider.Value;
        var steps = decimal.Round((raw - minimum) / step, 0, MidpointRounding.AwayFromZero);
        return minimum + steps * step;
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
                $"SSDPとプレフィックス/24以上の自動ping探索では対応アンプを検出できませんでした。\n\n" +
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
                System.Windows.MessageBox.Show(result.Message, "Yamaha AV Manager", MessageBoxButton.OK,
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
        var window = new HotkeySettingsWindow(
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
                    "Yamaha AV Manager",
                    MessageBoxButton.OK,
                    result.Outcome == ControlOutcome.Blocked ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(
                "操作を完了できませんでした。ログを確認してください。",
                "Yamaha AV Manager",
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
                    DeviceUnavailableException => "対応アンプに接続されていません。",
                    CapabilityNotSupportedException => "実機のCapabilityにこの操作がありません。",
                    OperationCanceledException => "操作がタイムアウトしました。",
                    ArgumentException => "接続先はIPアドレスまたはホスト名だけを指定してください。",
                    _ => "操作を完了できませんでした。ログを確認してください。"
                },
                "Yamaha AV Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string FormatVolume(MainZoneStatusResponse status)
    {
        if (status.ActualVolume?.Value is decimal actual)
        {
            return string.IsNullOrWhiteSpace(status.ActualVolume.Unit)
                ? actual.ToString("0.###", CultureInfo.CurrentCulture)
                : $"{actual.ToString("0.###", CultureInfo.CurrentCulture)} {status.ActualVolume.Unit}";
        }

        return status.Volume?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—";
    }

    private sealed record DiscoveryInterfaceOption(string Label, string? Id);
}
