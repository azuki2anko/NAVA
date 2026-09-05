using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using RxV4A.Core;
using RxV4A.Host;

namespace RxV4A.Desktop;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan DeviceConfirmationTimeout = TimeSpan.FromSeconds(4);
    private readonly IDeviceManager _deviceManager;
    private readonly IControlOrchestrator _orchestrator;
    private readonly IRegisteredActionService _registeredActions;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly GlobalHotkeyService _globalHotkeys;
    private readonly WindowsStartupService? _windowsStartup;
    private readonly object _settingsCommandQueueSync = new();
    private readonly Dictionary<string, long> _operationRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingOperationLabels = new(StringComparer.Ordinal);
    private Task _settingsCommandQueue = Task.CompletedTask;
    private HwndSource? _windowSource;
    private bool _allowClose;
    private bool _pingConfirmationPromptActive;
    private bool _updatingDailyControls;
    private string? _declinedPingConfirmationSignature;
    private string? _pendingConnectionText;
    private string? _pendingPower;
    private string? _pendingInput;
    private decimal? _pendingVolume;
    private bool? _pendingMute;
    private string? _pendingSoundProgram;
    private bool? _pendingSurround3d;
    private bool? _pendingDirect;
    private bool? _pendingPureDirect;
    private bool? _pendingEnhancer;
    private ToneControlSettings? _pendingToneControl;
    private EqualizerSettings? _pendingEqualizer;
    private decimal? _pendingBalance;

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
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        UpdateMinimizeBehaviorStatus();
        try
        {
            _windowsStartup = new WindowsStartupService();
            StartWithWindowsCheckBox.IsChecked = _windowsStartup.IsEnabled();
            UpdateStartWithWindowsStatus();
        }
        catch (Exception)
        {
            StartWithWindowsCheckBox.IsEnabled = false;
            StartWithWindowsStatusText.Text = "Windowsのスタートアップ設定を読み取れませんでした。";
        }
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowFromTray);
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        Activate();
        Focus();
        Topmost = true;
        Topmost = false;
        Activate();
    }

    public void StartMinimizedToTray()
    {
        ShowInTaskbar = false;
        _ = new WindowInteropHelper(this).EnsureHandle();
    }

    public void AllowClose() => _allowClose = true;

    public event EventHandler? MinimizedToTray;

    public Task ExecutePowerAsync(MainPower power)
    {
        BeginPowerChange(power);
        return Task.CompletedTask;
    }

    private void PowerOnButton_Click(object sender, RoutedEventArgs e) => BeginPowerChange(MainPower.On);

    private void StandbyButton_Click(object sender, RoutedEventArgs e) => BeginPowerChange(MainPower.Standby);

    private void BeginPowerChange(MainPower power)
    {
        var target = power == MainPower.On ? "on" : "standby";
        var revision = NextOperationRevision("power");
        _pendingPower = target;
        SetPendingOperation("power", power == MainPower.On ? "電源をONへ変更中" : "電源をスタンバイへ変更中");
        UpdateSnapshot(_deviceManager.Snapshot);

        EnqueueSettingsCommand(async () =>
        {
            try
            {
                var result = await _orchestrator.SetPowerAsync(power, allowBlockedOn: false).ConfigureAwait(false);
                var snapshot = result.Outcome is ControlOutcome.Succeeded or ControlOutcome.AlreadySatisfied
                    ? await ConfirmDeviceStateAsync(state =>
                            string.Equals(state.MainZone?.Power, target, StringComparison.OrdinalIgnoreCase))
                        .ConfigureAwait(false)
                    : _deviceManager.Snapshot;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentOperation("power", revision))
                    {
                        return;
                    }

                    _pendingPower = null;
                    ClearPendingOperation("power");
                    UpdateSnapshot(snapshot);
                    ShowControlResultIfNeeded(result);
                });
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() => FailOptimisticOperation(
                    "power", revision, () => _pendingPower = null, exception));
            }
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _declinedPingConfirmationSignature = null;
        _pendingConnectionText = "状態を確認中…";
        UpdateSnapshot(_deviceManager.Snapshot);
        await RunUiOperationAsync(() => _deviceManager.RefreshAsync());
        _pendingConnectionText = null;
        UpdateSnapshot(_deviceManager.Snapshot);
        MaybePromptForBroadPingScan(_deviceManager.Snapshot);
    }

    private async void SaveHostButton_Click(object sender, RoutedEventArgs e)
    {
        var interfaceId = (DiscoveryNetworkInterfaceComboBox.SelectedItem as DiscoveryInterfaceOption)?.Id;
        _declinedPingConfirmationSignature = null;
        _pendingConnectionText = "接続設定を反映中…";
        UpdateSnapshot(_deviceManager.Snapshot);
        await RunUiOperationAsync(() =>
            _deviceManager.UpdateConnectionSettingsAsync(ManualHostTextBox.Text, interfaceId));
        _pendingConnectionText = null;
        UpdateSnapshot(_deviceManager.Snapshot);
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

        if (_settings.MinimizeToTray)
        {
            MinimizeToTray();
        }
        else
        {
            ShowInTaskbar = true;
        }
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
            (wParam.ToInt64() & systemCommandMask) == systemCommandMinimize &&
            _settings.MinimizeToTray)
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

    private async void MinimizeToTrayCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var requested = MinimizeToTrayCheckBox.IsChecked == true;
        bool previous;
        lock (_settings.SyncRoot)
        {
            previous = _settings.MinimizeToTray;
            _settings.MinimizeToTray = requested;
        }

        UpdateMinimizeBehaviorStatus();
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (_settings.SyncRoot)
            {
                _settings.MinimizeToTray = previous;
            }

            MinimizeToTrayCheckBox.IsChecked = previous;
            UpdateMinimizeBehaviorStatus();
            ShowUiOperationError(exception);
        }
    }

    private void UpdateMinimizeBehaviorStatus()
    {
        MinimizeBehaviorStatusText.Text = _settings.MinimizeToTray
            ? "最小化するとタスクトレイへ収納します。"
            : "最小化してもタスクバーにアイコンを残します。";
    }

    private async void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_windowsStartup is null)
        {
            return;
        }

        var requested = StartWithWindowsCheckBox.IsChecked == true;
        var previous = !requested;
        StartWithWindowsCheckBox.IsEnabled = false;
        StartWithWindowsStatusText.Text = requested
            ? "Windows起動時の実行を登録しています…"
            : "Windows起動時の実行を解除しています…";

        try
        {
            await Task.Run(() => _windowsStartup.SetEnabled(requested));
            var confirmed = await Task.Run(_windowsStartup.IsEnabled);
            if (confirmed != requested)
            {
                throw new InvalidOperationException("スタートアップ設定を確認できませんでした。");
            }

            StartWithWindowsCheckBox.IsChecked = confirmed;
            UpdateStartWithWindowsStatus();
        }
        catch (Exception)
        {
            StartWithWindowsCheckBox.IsChecked = previous;
            StartWithWindowsStatusText.Text = "Windows起動時の実行設定を変更できませんでした。";
            System.Windows.MessageBox.Show(
                "Windows起動時の実行設定を変更できませんでした。",
                "NAVA",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            StartWithWindowsCheckBox.IsEnabled = true;
        }
    }

    private void UpdateStartWithWindowsStatus()
    {
        StartWithWindowsStatusText.Text = StartWithWindowsCheckBox.IsChecked == true
            ? "Windowsへのサインイン時にNAVAを自動起動します。"
            : "Windowsへのサインイン時には自動起動しません。";
    }

    private void DeviceManager_SnapshotChanged(object? sender, DeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => UpdateSnapshot(snapshot));

    private void Orchestrator_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateControlState);

    private void UpdateSnapshot(DeviceSnapshot snapshot)
    {
        ConnectionText.Text = _pendingConnectionText ?? (snapshot.ConnectionState switch
        {
            DeviceConnectionState.Connected => "接続中",
            DeviceConnectionState.Connecting => "接続しています…",
            DeviceConnectionState.Reconnecting => "再接続しています…",
            DeviceConnectionState.Unsupported => "対象外の機器",
            _ when snapshot.ErrorCode == "ping_scan_confirmation_required" => "ping探索の確認待ち",
            _ when snapshot.ErrorCode == "ping_scan_too_large" => "サブネットが広すぎます",
            _ => "未接続"
        });
        DeviceText.Text = snapshot.Capabilities is null
            ? snapshot.ErrorCode switch
            {
                "ping_scan_confirmation_required" => "広いサブネットのping探索には確認が必要です",
                "ping_scan_too_large" => "サブネットが/16未満です。接続先を手動指定してください",
                "device_not_found" => "SSDPおよびping探索で見つかりません",
                _ => "—"
            }
            : $"{snapshot.Capabilities.DeviceInfo.ModelName}  (API {snapshot.Capabilities.DeviceInfo.ApiVersion})";
        PowerText.Text = _pendingPower ?? snapshot.MainZone?.Power ?? "—";
        var playbackInput = _pendingInput ?? snapshot.MainZone?.Input ?? "—";
        var playbackVolume = _pendingVolume is decimal pendingVolume
            ? $"LEVEL {FormatValue(pendingVolume)}"
            : snapshot.MainZone is null
                ? "—"
                : FormatVolume(snapshot.MainZone);
        PlaybackText.Text = $"{playbackInput} / {playbackVolume}";
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
            var displayedInput = _pendingInput ?? status?.Input;
            InputComboBox.SelectedItem = inputs.FirstOrDefault(input =>
                string.Equals(input, displayedInput, StringComparison.OrdinalIgnoreCase));
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
                var displayedVolume = _pendingVolume ?? status?.Volume;
                if (displayedVolume is decimal volume)
                {
                    VolumeSlider.Value = Math.Clamp((double)volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
                }

                VolumeValueText.Text = FormatValue((decimal)VolumeSlider.Value);
                VolumeSlider.ToolTip = $"{volumeRange.Minimum} ～ {volumeRange.Maximum} / {volumeRange.Step}刻み";
            }

            SetBooleanControl(MutePanel, MuteCheckBox, capabilities, "mute", _pendingMute ?? status?.Mute, connected);

            var programs = zone?.SoundPrograms.ToArray() ?? [];
            var hasPrograms = capabilities?.SupportsZoneFunction("main", "sound_program") == true &&
                              programs.Length > 0;
            SoundProgramPanel.Visibility = hasPrograms ? Visibility.Visible : Visibility.Collapsed;
            SoundProgramComboBox.ItemsSource = programs;
            var displayedProgram = _pendingSoundProgram ?? status?.SoundProgram;
            SoundProgramComboBox.SelectedItem = programs.FirstOrDefault(program =>
                string.Equals(program, displayedProgram, StringComparison.OrdinalIgnoreCase));
            SoundProgramComboBox.IsEnabled = connected;
            SetSoundProgramButton.IsEnabled = connected;

            SetBooleanControl(null, Surround3dCheckBox, capabilities, "surround_3d", _pendingSurround3d ?? status?.Surround3d, connected);
            SetBooleanControl(null, DirectCheckBox, capabilities, "direct", _pendingDirect ?? status?.Direct, connected);
            SetBooleanControl(null, PureDirectCheckBox, capabilities, "pure_direct", _pendingPureDirect ?? status?.PureDirect, connected);
            SetBooleanControl(null, EnhancerCheckBox, capabilities, "enhancer", _pendingEnhancer ?? status?.Enhancer, connected);
            ProcessingPanel.Visibility = ProcessingPanel.Children.OfType<System.Windows.Controls.CheckBox>()
                .Any(control => control.Visibility == Visibility.Visible)
                ? Visibility.Visible
                : Visibility.Collapsed;

            var hasTone = capabilities?.SupportsZoneFunction("main", "tone_control") == true;
            TonePanel.Visibility = hasTone ? Visibility.Visible : Visibility.Collapsed;
            TonePanel.IsEnabled = connected;
            if (hasTone)
            {
                var tone = _pendingToneControl;
                SetModes(ToneModeComboBox, zone!.ToneControlModes, tone?.Mode ?? status?.ToneControl?.Mode);
                BassTextBox.Text = FormatValue(tone?.Bass ?? status?.ToneControl?.Bass);
                TrebleTextBox.Text = FormatValue(tone?.Treble ?? status?.ToneControl?.Treble);
                SetRangeToolTip(BassTextBox, capabilities!.FindZoneRange("main", "tone_control"));
                SetRangeToolTip(TrebleTextBox, capabilities.FindZoneRange("main", "tone_control"));
            }

            var hasEqualizer = capabilities?.SupportsZoneFunction("main", "equalizer") == true;
            EqualizerPanel.Visibility = hasEqualizer ? Visibility.Visible : Visibility.Collapsed;
            EqualizerPanel.IsEnabled = connected;
            if (hasEqualizer)
            {
                var equalizer = _pendingEqualizer;
                SetModes(EqualizerModeComboBox, zone!.EqualizerModes, equalizer?.Mode ?? status?.Equalizer?.Mode);
                EqualizerLowTextBox.Text = FormatValue(equalizer?.Low ?? status?.Equalizer?.Low);
                EqualizerMidTextBox.Text = FormatValue(equalizer?.Mid ?? status?.Equalizer?.Mid);
                EqualizerHighTextBox.Text = FormatValue(equalizer?.High ?? status?.Equalizer?.High);
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
                BalanceTextBox.Text = FormatValue(_pendingBalance ?? status?.Balance);
                SetRangeToolTip(BalanceTextBox, balanceRange);
            }

            CapabilityNoteText.Text = capabilities is null
                ? "接続後、機器の公式APIから取得した対応機能だけを表示します。"
                : string.Equals(capabilities.DeviceInfo.ModelName, "RX-V4A", StringComparison.OrdinalIgnoreCase)
                    ? "RX-V4A実機確認対象。未対応の機能は表示しません。"
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

    private void SetInputButton_Click(object sender, RoutedEventArgs e)
    {
        if (InputComboBox.SelectedItem is string input)
        {
            BeginOptimisticDeviceChange(
                "input",
                $"ソースを {input} へ変更中",
                () => _pendingInput = input,
                () => _pendingInput = null,
                () => _deviceManager.SetMainInputAsync(input),
                snapshot => string.Equals(snapshot.MainZone?.Input, input, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void SetVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        var volume = GetSnappedVolume();
        var tolerance = (decimal)VolumeSlider.TickFrequency / 2m;
        BeginOptimisticDeviceChange(
            "volume",
            $"音量を {FormatValue(volume)} へ変更中",
            () => _pendingVolume = volume,
            () => _pendingVolume = null,
            () => _deviceManager.SetMainVolumeAsync(volume),
            snapshot => snapshot.MainZone?.Volume is decimal actual && Math.Abs(actual - volume) <= tolerance);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText is not null)
        {
            VolumeValueText.Text = FormatValue((decimal)e.NewValue);
        }
    }

    private void MuteCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var value = MuteCheckBox.IsChecked == true;
        BeginBooleanOperation("mute", "ミュート", value, state => _pendingMute = state,
            state => state.MainZone?.Mute, target => _deviceManager.SetMainMuteAsync(target));
    }

    private void SetSoundProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (SoundProgramComboBox.SelectedItem is string program)
        {
            BeginOptimisticDeviceChange(
                "sound-program",
                $"音場を {program} へ変更中",
                () => _pendingSoundProgram = program,
                () => _pendingSoundProgram = null,
                () => _deviceManager.SetMainSoundProgramAsync(program),
                snapshot => string.Equals(
                    snapshot.MainZone?.SoundProgram, program, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Surround3dCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var value = Surround3dCheckBox.IsChecked == true;
        BeginBooleanOperation("surround-3d", "3D Surround", value, state => _pendingSurround3d = state,
            state => state.MainZone?.Surround3d, target => _deviceManager.SetMainSurround3dAsync(target));
    }

    private void DirectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var value = DirectCheckBox.IsChecked == true;
        BeginBooleanOperation("direct", "Direct", value, state => _pendingDirect = state,
            state => state.MainZone?.Direct, target => _deviceManager.SetMainDirectAsync(target));
    }

    private void PureDirectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var value = PureDirectCheckBox.IsChecked == true;
        BeginBooleanOperation("pure-direct", "Pure Direct", value, state => _pendingPureDirect = state,
            state => state.MainZone?.PureDirect, target => _deviceManager.SetMainPureDirectAsync(target));
    }

    private void EnhancerCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var value = EnhancerCheckBox.IsChecked == true;
        BeginBooleanOperation("enhancer", "Enhancer", value, state => _pendingEnhancer = state,
            state => state.MainZone?.Enhancer, target => _deviceManager.SetMainEnhancerAsync(target));
    }

    private void BeginBooleanOperation(
        string key,
        string displayName,
        bool target,
        Action<bool?> setPending,
        Func<DeviceSnapshot, bool?> readValue,
        Func<bool, Task<DeviceSnapshot>> operation)
    {
        if (_updatingDailyControls)
        {
            return;
        }

        BeginOptimisticDeviceChange(
            key,
            $"{displayName}を{(target ? "ON" : "OFF")}へ変更中",
            () => setPending(target),
            () => setPending(null),
            () => operation(target),
            snapshot => readValue(snapshot) == target);
    }

    private void SetToneButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = new ToneControlSettings(
                ToneModeComboBox.SelectedItem as string,
                ParseOptionalDecimal(BassTextBox.Text),
                ParseOptionalDecimal(TrebleTextBox.Text));
            BeginOptimisticDeviceChange(
                "tone",
                "トーン設定を反映中",
                () => _pendingToneControl = target,
                () => _pendingToneControl = null,
                () => _deviceManager.SetMainToneControlAsync(target),
                snapshot => ToneControlMatches(snapshot.MainZone?.ToneControl, target));
        }
        catch (Exception exception)
        {
            ShowUiOperationError(exception);
        }
    }

    private void SetEqualizerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = new EqualizerSettings(
                EqualizerModeComboBox.SelectedItem as string,
                ParseOptionalDecimal(EqualizerLowTextBox.Text),
                ParseOptionalDecimal(EqualizerMidTextBox.Text),
                ParseOptionalDecimal(EqualizerHighTextBox.Text));
            BeginOptimisticDeviceChange(
                "equalizer",
                "EQ設定を反映中",
                () => _pendingEqualizer = target,
                () => _pendingEqualizer = null,
                () => _deviceManager.SetMainEqualizerAsync(target),
                snapshot => EqualizerMatches(snapshot.MainZone?.Equalizer, target));
        }
        catch (Exception exception)
        {
            ShowUiOperationError(exception);
        }
    }

    private void SetBalanceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = ParseOptionalDecimal(BalanceTextBox.Text) ??
                         throw new ArgumentException("バランス値を入力してください。");
            BeginOptimisticDeviceChange(
                "balance",
                $"バランスを {FormatValue(target)} へ変更中",
                () => _pendingBalance = target,
                () => _pendingBalance = null,
                () => _deviceManager.SetMainBalanceAsync(target),
                snapshot => snapshot.MainZone?.Balance == target);
        }
        catch (Exception exception)
        {
            ShowUiOperationError(exception);
        }
    }

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

    private async Task ExecuteHotkeyActionAsync(GlobalHotkeyBinding binding)
    {
        var actionId = binding.ActionId ?? string.Empty;
        if (string.Equals(actionId, HotkeyActionIds.PowerOn, StringComparison.OrdinalIgnoreCase))
        {
            await RunControlOperationAsync(() => _orchestrator.SetPowerAsync(MainPower.On, false));
        }
        else if (string.Equals(actionId, HotkeyActionIds.PowerStandby, StringComparison.OrdinalIgnoreCase))
        {
            await RunControlOperationAsync(() => _orchestrator.SetPowerAsync(MainPower.Standby, false));
        }
        else if (string.Equals(actionId, HotkeyActionIds.PowerToggle, StringComparison.OrdinalIgnoreCase))
        {
            await TogglePowerByHotkeyAsync();
        }
        else if (string.Equals(actionId, HotkeyActionIds.MuteOn, StringComparison.OrdinalIgnoreCase))
        {
            await RunUiOperationAsync(() => _deviceManager.SetMainMuteAsync(true));
        }
        else if (string.Equals(actionId, HotkeyActionIds.MuteOff, StringComparison.OrdinalIgnoreCase))
        {
            await RunUiOperationAsync(() => _deviceManager.SetMainMuteAsync(false));
        }
        else if (string.Equals(actionId, HotkeyActionIds.MuteToggle, StringComparison.OrdinalIgnoreCase))
        {
            var muted = _deviceManager.Snapshot.MainZone?.Mute ?? false;
            await RunUiOperationAsync(() => _deviceManager.SetMainMuteAsync(!muted));
        }
        else if (HotkeyActionIds.RequiresAmount(actionId))
        {
            var direction = string.Equals(actionId, HotkeyActionIds.VolumeUp, StringComparison.OrdinalIgnoreCase)
                ? 1m
                : -1m;
            await RunUiOperationAsync(() => ChangeVolumeByHotkeyAsync(direction * (binding.Amount ?? 1m)));
        }
        else if (actionId.StartsWith("input:", StringComparison.OrdinalIgnoreCase))
        {
            await RunUiOperationAsync(() => _deviceManager.SetMainInputAsync(actionId[6..]));
        }
        else if (actionId.StartsWith("sound-program:", StringComparison.OrdinalIgnoreCase))
        {
            await RunUiOperationAsync(() => _deviceManager.SetMainSoundProgramAsync(actionId[14..]));
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
                System.Windows.MessageBox.Show(result.Message, "NAVA", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private async Task TogglePowerByHotkeyAsync()
    {
        var snapshot = _deviceManager.Snapshot;
        if (snapshot.MainZone?.Power is null)
        {
            await _deviceManager.RefreshAsync();
            snapshot = _deviceManager.Snapshot;
        }

        var power = string.Equals(snapshot.MainZone?.Power, "on", StringComparison.OrdinalIgnoreCase)
            ? MainPower.Standby
            : MainPower.On;
        await RunControlOperationAsync(() => _orchestrator.SetPowerAsync(power, false));
    }

    private Task<DeviceSnapshot> ChangeVolumeByHotkeyAsync(decimal delta)
    {
        var snapshot = _deviceManager.Snapshot;
        var current = snapshot.MainZone?.Volume
            ?? throw new DeviceUnavailableException("The current main-zone volume is unavailable.");
        var range = snapshot.Capabilities?.FindZoneRange("main", "volume")
            ?? throw new CapabilityNotSupportedException("main.volume.range");
        if (range.Minimum is not decimal minimum || range.Maximum is not decimal maximum ||
            range.Step is not decimal step || step <= 0)
        {
            throw new CapabilityNotSupportedException("main.volume.range");
        }

        var raw = Math.Clamp(current + delta, minimum, maximum);
        var steps = decimal.Round((raw - minimum) / step, 0, MidpointRounding.AwayFromZero);
        return _deviceManager.SetMainVolumeAsync(Math.Clamp(minimum + steps * step, minimum, maximum));
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
            _globalHotkeys,
            _deviceManager,
            _registeredActions)
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

    private void BeginOptimisticDeviceChange(
        string key,
        string pendingLabel,
        Action applyPending,
        Action clearPending,
        Func<Task> changeDeviceState,
        Func<DeviceSnapshot, bool> targetReached)
    {
        var revision = NextOperationRevision(key);
        applyPending();
        SetPendingOperation(key, pendingLabel);
        UpdateSnapshot(_deviceManager.Snapshot);

        EnqueueSettingsCommand(async () =>
        {
            var isCurrent = await Dispatcher.InvokeAsync(() => IsCurrentOperation(key, revision));
            if (!isCurrent)
            {
                return;
            }

            try
            {
                await changeDeviceState().ConfigureAwait(false);
                var snapshot = await ConfirmDeviceStateAsync(targetReached).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentOperation(key, revision))
                    {
                        return;
                    }

                    clearPending();
                    ClearPendingOperation(key);
                    UpdateSnapshot(snapshot);
                });
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() =>
                    FailOptimisticOperation(key, revision, clearPending, exception));
            }
        });
    }

    private long NextOperationRevision(string key)
    {
        var revision = _operationRevisions.TryGetValue(key, out var current) ? current + 1 : 1;
        _operationRevisions[key] = revision;
        return revision;
    }

    private bool IsCurrentOperation(string key, long revision) =>
        _operationRevisions.TryGetValue(key, out var current) && current == revision;

    private void SetPendingOperation(string key, string label)
    {
        _pendingOperationLabels[key] = label;
        UpdateOperationStatus();
    }

    private void ClearPendingOperation(string key)
    {
        _pendingOperationLabels.Remove(key);
        UpdateOperationStatus();
    }

    private void UpdateOperationStatus()
    {
        OperationStatusText.Visibility = _pendingOperationLabels.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        OperationStatusText.Text = _pendingOperationLabels.Count == 0
            ? string.Empty
            : $"{string.Join("  •  ", _pendingOperationLabels.Values)}  •  実機確認中";
    }

    private void EnqueueSettingsCommand(Func<Task> command)
    {
        lock (_settingsCommandQueueSync)
        {
            _settingsCommandQueue = _settingsCommandQueue.ContinueWith(
                    async _ => await command().ConfigureAwait(false),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task<DeviceSnapshot> ConfirmDeviceStateAsync(Func<DeviceSnapshot, bool> targetReached)
    {
        var delays = new[]
        {
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(600),
            TimeSpan.FromMilliseconds(1000)
        };
        using var timeout = new CancellationTokenSource(DeviceConfirmationTimeout);
        var snapshot = _deviceManager.Snapshot;
        if (targetReached(snapshot))
        {
            return snapshot;
        }

        try
        {
            foreach (var delay in delays)
            {
                await Task.Delay(delay, timeout.Token).ConfigureAwait(false);
                await _deviceManager.RefreshAsync(timeout.Token).ConfigureAwait(false);
                snapshot = _deviceManager.Snapshot;
                if (targetReached(snapshot))
                {
                    return snapshot;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The device status confirmation timed out.");
        }

        throw new TimeoutException("The device did not report the requested state before the confirmation timeout.");
    }

    private void FailOptimisticOperation(
        string key,
        long revision,
        Action clearPending,
        Exception exception)
    {
        if (!IsCurrentOperation(key, revision))
        {
            return;
        }

        clearPending();
        ClearPendingOperation(key);
        UpdateSnapshot(_deviceManager.Snapshot);
        ShowUiOperationError(exception);
    }

    private static bool ToneControlMatches(ToneControlStatus? actual, ToneControlSettings target) =>
        actual is not null &&
        (target.Mode is null || string.Equals(actual.Mode, target.Mode, StringComparison.OrdinalIgnoreCase)) &&
        (target.Bass is null || actual.Bass == target.Bass) &&
        (target.Treble is null || actual.Treble == target.Treble);

    private static bool EqualizerMatches(EqualizerStatus? actual, EqualizerSettings target) =>
        actual is not null &&
        (target.Mode is null || string.Equals(actual.Mode, target.Mode, StringComparison.OrdinalIgnoreCase)) &&
        (target.Low is null || actual.Low == target.Low) &&
        (target.Mid is null || actual.Mid == target.Mid) &&
        (target.High is null || actual.High == target.High);

    private static void ShowControlResultIfNeeded(ControlOperationResult result)
    {
        if (result.Outcome is not (ControlOutcome.Blocked or ControlOutcome.PartialFailure or ControlOutcome.Failed))
        {
            return;
        }

        System.Windows.MessageBox.Show(
            result.Message + (result.ActiveBlockers.Count > 0
                ? $"\n有効なコンテキスト: {string.Join(", ", result.ActiveBlockers)}"
                : string.Empty),
            "NAVA",
            MessageBoxButton.OK,
            result.Outcome == ControlOutcome.Blocked ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task RunControlOperationAsync(Func<Task<ControlOperationResult>> operation)
    {
        try
        {
            var result = await operation();
            UpdateControlState();
            ShowControlResultIfNeeded(result);
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(
                "操作を完了できませんでした。ログを確認してください。",
                "NAVA",
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
            ShowUiOperationError(exception);
        }
    }

    private static void ShowUiOperationError(Exception exception)
    {
        System.Windows.MessageBox.Show(
            exception switch
            {
                DeviceUnavailableException => "対応アンプに接続されていません。",
                CapabilityNotSupportedException => "実機のCapabilityにこの操作がありません。",
                TimeoutException => "実機の状態確認がタイムアウトしました。現在の状態に表示を戻します。",
                OperationCanceledException => "操作がタイムアウトしました。",
                ArgumentException => "入力内容を確認してください。",
                _ => "操作を完了できませんでした。ログを確認してください。"
            },
            "NAVA",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
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
