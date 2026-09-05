using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RxV4A.Core;
using RxV4A.Host;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace RxV4A.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan DeviceConfirmationTimeout = TimeSpan.FromSeconds(4);
    private readonly IDeviceManager _deviceManager;
    private readonly IControlOrchestrator _orchestrator;
    private readonly SettingsWindow _settingsWindow;
    private readonly object _commandQueueSync = new();
    private Task _commandQueue = Task.CompletedTask;
    private bool _allowClose;
    private bool _updatingControls;
    private bool _volumeDragging;
    private bool? _pendingPowerState;
    private bool? _pendingMuteState;
    private decimal? _pendingVolume;
    private string? _pendingInput;
    private string? _pendingSoundProgram;
    private bool _refreshInProgress;
    private long _powerRevision;
    private long _muteRevision;
    private long _volumeRevision;
    private long _inputRevision;
    private long _soundProgramRevision;
    private long _refreshRevision;
    private decimal _volumeMinimum = -80m;
    private decimal _volumeMaximum = 16.5m;
    private decimal _volumeStep = 0.5m;
    private decimal _displayedVolume = -40m;

    public MainWindow(
        IDeviceManager deviceManager,
        IControlOrchestrator orchestrator,
        SettingsWindow settingsWindow)
    {
        _deviceManager = deviceManager;
        _orchestrator = orchestrator;
        _settingsWindow = settingsWindow;
        InitializeComponent();
        _deviceManager.SnapshotChanged += DeviceManager_SnapshotChanged;
        _orchestrator.StateChanged += Orchestrator_StateChanged;
        UpdateSnapshot(_deviceManager.Snapshot);
    }

    public void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowFromTray);
            return;
        }

        // A minimized window is hidden by Window_StateChanged. Restore its state before
        // showing it, otherwise WPF can keep the hidden minimized presentation alive.
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

        // Bring an already-visible window in front without leaving it permanently topmost.
        Topmost = true;
        Topmost = false;
        Activate();
    }

    public void AllowClose()
    {
        _allowClose = true;
        _deviceManager.SnapshotChanged -= DeviceManager_SnapshotChanged;
        _orchestrator.StateChanged -= Orchestrator_StateChanged;
    }

    public Task ExecutePowerAsync(MainPower power)
    {
        if (Dispatcher.CheckAccess())
        {
            BeginPowerChange(power);
        }
        else
        {
            Dispatcher.BeginInvoke(() => BeginPowerChange(power));
        }

        return Task.CompletedTask;
    }

    private void DeviceManager_SnapshotChanged(object? sender, DeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => UpdateSnapshot(snapshot));

    private void Orchestrator_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => UpdateSnapshot(_deviceManager.Snapshot));

    private void UpdateSnapshot(DeviceSnapshot snapshot)
    {
        _updatingControls = true;
        try
        {
            var connected = snapshot.ConnectionState == DeviceConnectionState.Connected;
            var status = snapshot.MainZone;
            var capabilities = snapshot.Capabilities;
            var zone = capabilities?.FindZone("main");

            ConnectionText.Text = _refreshInProgress
                ? "状態を確認中…"
                : snapshot.ConnectionState switch
                {
                    DeviceConnectionState.Connected => "接続済み",
                    DeviceConnectionState.Connecting => "接続中…",
                    DeviceConnectionState.Reconnecting => "再接続中…",
                    DeviceConnectionState.Unsupported => "未対応の機器",
                    _ => "未接続"
                };
            DeviceText.Text = _refreshInProgress
                ? "ネットワークから最新状態を取得しています"
                : capabilities is null
                ? SnapshotMessage(snapshot)
                : $"{capabilities.DeviceInfo.ModelName}  •  API {capabilities.DeviceInfo.ApiVersion}";
            ConnectionIndicator.Fill = _refreshInProgress
                ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
                : connected
                ? new SolidColorBrush(Color.FromRgb(101, 230, 209))
                : snapshot.ConnectionState is DeviceConnectionState.Connecting or DeviceConnectionState.Reconnecting
                    ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
                    : new SolidColorBrush(Color.FromRgb(100, 116, 139));

            var powerOn = _pendingPowerState ??
                          string.Equals(status?.Power, "on", StringComparison.OrdinalIgnoreCase);
            ApplyPowerVisual(powerOn);
            PowerToggle.IsEnabled = connected &&
                                    capabilities?.SupportsZoneFunction("main", "power") == true;

            var hasMute = capabilities?.SupportsZoneFunction("main", "mute") == true;
            MuteToggle.Visibility = hasMute ? Visibility.Visible : Visibility.Collapsed;
            ApplyMuteVisual(_pendingMuteState ?? status?.Mute == true);
            MuteToggle.IsEnabled = connected;

            var inputs = zone?.Inputs.Select(input => input.Id)
                .Where(ApplicationScope.IsOperationalInput).ToArray() ?? [];
            SourceList.ItemsSource = inputs;
            var displayedInput = _pendingInput ?? status?.Input;
            SourceList.SelectedItem = inputs.FirstOrDefault(input =>
                string.Equals(input, displayedInput, StringComparison.OrdinalIgnoreCase));
            SourceText.Text = displayedInput ?? "—";
            SourceButton.IsEnabled = connected && inputs.Length > 0;

            var programs = zone?.SoundPrograms.ToArray() ?? [];
            SoundFieldList.ItemsSource = programs;
            var displayedProgram = _pendingSoundProgram ?? status?.SoundProgram;
            SoundFieldList.SelectedItem = programs.FirstOrDefault(program =>
                string.Equals(program, displayedProgram, StringComparison.OrdinalIgnoreCase));
            SoundFieldText.Text = displayedProgram ?? "—";
            SoundFieldButton.IsEnabled = connected && programs.Length > 0;

            var range = capabilities?.FindZoneRange("main", "volume");
            var hasVolume = capabilities?.SupportsZoneFunction("main", "volume") == true &&
                            range?.Minimum is decimal && range.Maximum is decimal && range.Step is > 0;
            if (hasVolume)
            {
                _volumeMinimum = range!.Minimum!.Value;
                _volumeMaximum = range.Maximum!.Value;
                _volumeStep = range.Step!.Value;
            }

            if (!_volumeDragging && _pendingVolume is decimal pendingVolume)
            {
                _displayedVolume = Math.Clamp(pendingVolume, _volumeMinimum, _volumeMaximum);
            }
            else if (status?.Volume is decimal volume && !_volumeDragging)
            {
                _displayedVolume = Math.Clamp(volume, _volumeMinimum, _volumeMaximum);
            }

            VolumeDial.IsEnabled = connected && hasVolume;
            VolumeDownButton.IsEnabled = VolumeDial.IsEnabled;
            VolumeUpButton.IsEnabled = VolumeDial.IsEnabled;
            VolumeDial.Opacity = hasVolume ? 1 : 0.38;
            UpdateVolumeVisual();
            UpdateActualVolumeVisual(status);
            if (status?.Volume is not decimal && _pendingVolume is null)
            {
                VolumeValueText.Text = "—";
                VolumeUnitText.Text = string.Empty;
            }
            else
            {
                VolumeUnitText.Text = _volumeDragging || _pendingVolume is not null ? "調整中" : "LEVEL";
            }

            var blockers = _orchestrator.ActiveBlockers;
            var optimisticStatus = GetOptimisticStatusText();
            StatusText.Text = optimisticStatus ?? (blockers.Count > 0
                ? $"電源ON禁止中  •  {string.Join(", ", blockers)}"
                : connected
                    ? $"最終更新  {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}"
                    : SnapshotMessage(snapshot));
            StatusText.Foreground = optimisticStatus is not null
                ? new SolidColorBrush(Color.FromRgb(101, 230, 209))
                : blockers.Count > 0
                ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
                : new SolidColorBrush(Color.FromRgb(139, 154, 176));
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private static string SnapshotMessage(DeviceSnapshot snapshot) => snapshot.ErrorCode switch
    {
        "ping_scan_confirmation_required" => "設定画面でネットワーク探索を確認してください",
        "ping_scan_too_large" => "設定画面で接続先を指定してください",
        "device_not_found" => "対応アンプが見つかりません",
        _ => "アンプを検索しています…"
    };

    private string? GetOptimisticStatusText()
    {
        if (_refreshInProgress)
        {
            return "最新状態を取得中…";
        }

        if (_pendingPowerState is bool powerOn)
        {
            return $"電源を{(powerOn ? "ON" : "STANDBY")}へ変更中  •  実機確認中";
        }

        if (_pendingMuteState is bool muted)
        {
            return $"ミュートを{(muted ? "ON" : "OFF")}へ変更中  •  実機確認中";
        }

        if (_pendingVolume is decimal volume)
        {
            return $"音量を {volume.ToString("0.#", CultureInfo.CurrentCulture)} へ変更中  •  実機確認中";
        }

        if (_pendingInput is not null)
        {
            return $"ソースを {_pendingInput} へ変更中  •  実機確認中";
        }

        return _pendingSoundProgram is not null
            ? $"音場を {_pendingSoundProgram} へ変更中  •  実機確認中"
            : null;
    }

    private void PowerToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_updatingControls || !PowerToggle.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        PowerToggle.Focus();
        var targetIsOn = !string.Equals(PowerToggle.Tag as string, "On", StringComparison.Ordinal);
        BeginPowerChange(targetIsOn ? MainPower.On : MainPower.Standby);
    }

    private void BeginPowerChange(MainPower power)
    {
        var targetIsOn = power == MainPower.On;
        var revision = ++_powerRevision;
        _pendingPowerState = targetIsOn;
        ApplyPowerVisual(targetIsOn);
        ApplyPendingStatusVisual();

        EnqueueCommand(async () =>
        {
            try
            {
                var result = await _orchestrator.SetPowerAsync(power, allowBlockedOn: false)
                    .ConfigureAwait(false);
                var snapshot = result.Outcome is ControlOutcome.Succeeded or ControlOutcome.AlreadySatisfied
                    ? await ConfirmDeviceStateAsync(snapshot =>
                            string.Equals(
                                snapshot.MainZone?.Power,
                                targetIsOn ? "on" : "standby",
                                StringComparison.OrdinalIgnoreCase))
                        .ConfigureAwait(false)
                    : _deviceManager.Snapshot;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (revision == _powerRevision)
                    {
                        _pendingPowerState = null;
                        UpdateSnapshot(snapshot);
                    }

                    if (result.Outcome is ControlOutcome.Blocked or ControlOutcome.PartialFailure or ControlOutcome.Failed)
                    {
                        MessageBox.Show(result.Message, "NAVA", MessageBoxButton.OK,
                            result.Outcome == ControlOutcome.Blocked
                                ? MessageBoxImage.Information
                                : MessageBoxImage.Warning);
                    }
                });
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() => FailPendingOperation(
                    revision == _powerRevision,
                    () => _pendingPowerState = null,
                    exception));
            }
        });
    }

    private void MuteToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_updatingControls && MuteToggle.IsEnabled)
        {
            e.Handled = true;
            MuteToggle.Focus();
            var targetIsMuted = !string.Equals(MuteToggle.Tag as string, "On", StringComparison.Ordinal);
            BeginMuteChange(targetIsMuted);
        }
    }

    private void BeginMuteChange(bool targetIsMuted)
    {
        var revision = ++_muteRevision;
        _pendingMuteState = targetIsMuted;
        ApplyMuteVisual(targetIsMuted);
        ApplyPendingStatusVisual();
        EnqueueDeviceChange(
            () => _deviceManager.SetMainMuteAsync(targetIsMuted),
            snapshot => snapshot.MainZone?.Mute == targetIsMuted,
            () => revision == _muteRevision,
            () => _pendingMuteState = null);
    }

    private void ApplyPowerVisual(bool isOn)
    {
        PowerToggle.Tag = isOn ? "On" : "Off";
        PowerStateText.Text = isOn ? "POWER ON" : "STANDBY";
        PowerStateText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(101, 230, 209))
            : Brushes.WhiteSmoke;
    }

    private void ApplyMuteVisual(bool isMuted)
    {
        MuteToggle.Tag = isMuted ? "On" : "Off";
        MuteStateText.Text = isMuted ? "ON" : "OFF";
        MuteStateText.Foreground = isMuted
            ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
            : Brushes.WhiteSmoke;
    }

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        SourcePopup.IsOpen = !SourcePopup.IsOpen;
        if (SourceList.SelectedItem is not null)
        {
            SourceList.ScrollIntoView(SourceList.SelectedItem);
        }
    }

    private void SourceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingControls || !SourcePopup.IsOpen || SourceList.SelectedItem is not string input)
        {
            return;
        }

        SourcePopup.IsOpen = false;
        var revision = ++_inputRevision;
        _pendingInput = input;
        SourceText.Text = input;
        ApplyPendingStatusVisual();
        EnqueueDeviceChange(
            () => _deviceManager.SetMainInputAsync(input),
            snapshot => string.Equals(snapshot.MainZone?.Input, input, StringComparison.OrdinalIgnoreCase),
            () => revision == _inputRevision,
            () => _pendingInput = null);
    }

    private void SoundFieldButton_Click(object sender, RoutedEventArgs e)
    {
        SoundFieldPopup.IsOpen = !SoundFieldPopup.IsOpen;
        if (SoundFieldList.SelectedItem is not null)
        {
            SoundFieldList.ScrollIntoView(SoundFieldList.SelectedItem);
        }
    }

    private void SoundFieldList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingControls || !SoundFieldPopup.IsOpen || SoundFieldList.SelectedItem is not string program)
        {
            return;
        }

        SoundFieldPopup.IsOpen = false;
        var revision = ++_soundProgramRevision;
        _pendingSoundProgram = program;
        SoundFieldText.Text = program;
        ApplyPendingStatusVisual();
        EnqueueDeviceChange(
            () => _deviceManager.SetMainSoundProgramAsync(program),
            snapshot => string.Equals(
                snapshot.MainZone?.SoundProgram,
                program,
                StringComparison.OrdinalIgnoreCase),
            () => revision == _soundProgramRevision,
            () => _pendingSoundProgram = null);
    }

    private void VolumeDial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!VolumeDial.IsEnabled)
        {
            return;
        }

        _volumeDragging = true;
        VolumeDial.CaptureMouse();
        SetVolumeFromPoint(e.GetPosition(VolumeDial));
    }

    private void VolumeDial_MouseMove(object sender, MouseEventArgs e)
    {
        if (_volumeDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            SetVolumeFromPoint(e.GetPosition(VolumeDial));
        }
    }

    private void VolumeDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volumeDragging)
        {
            return;
        }

        SetVolumeFromPoint(e.GetPosition(VolumeDial));
        _volumeDragging = false;
        VolumeDial.ReleaseMouseCapture();
        QueueVolumeChange();
    }

    private void VolumeDial_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!VolumeDial.IsEnabled)
        {
            return;
        }

        ChangeVolume(e.Delta > 0 ? _volumeStep : -_volumeStep);
        QueueVolumeChange();
        e.Handled = true;
    }

    private void VolumeDownButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!VolumeDownButton.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        ChangeVolume(-_volumeStep);
        QueueVolumeChange();
    }

    private void VolumeUpButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!VolumeUpButton.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        ChangeVolume(_volumeStep);
        QueueVolumeChange();
    }

    private void ChangeVolume(decimal delta)
    {
        _displayedVolume = SnapVolume(_displayedVolume + delta);
        _pendingVolume = _displayedVolume;
        UpdateVolumeVisual();
        VolumeUnitText.Text = "調整中";
        ApplyPendingStatusVisual();
    }

    private void SetVolumeFromPoint(Point point)
    {
        var center = VolumeDial.ActualWidth / 2d;
        var angle = Math.Atan2(point.X - center, center - point.Y) * 180d / Math.PI;
        angle = Math.Clamp(angle, -135d, 135d);
        var ratio = (angle + 135d) / 270d;
        _displayedVolume = SnapVolume(_volumeMinimum + (decimal)ratio * (_volumeMaximum - _volumeMinimum));
        _pendingVolume = _displayedVolume;
        UpdateVolumeVisual();
        VolumeUnitText.Text = "調整中";
        ApplyPendingStatusVisual();
    }

    private decimal SnapVolume(decimal value)
    {
        var steps = Math.Round((value - _volumeMinimum) / _volumeStep, MidpointRounding.AwayFromZero);
        return Math.Clamp(_volumeMinimum + steps * _volumeStep, _volumeMinimum, _volumeMaximum);
    }

    private void QueueVolumeChange()
    {
        if (_pendingVolume is not decimal targetVolume)
        {
            return;
        }

        var revision = ++_volumeRevision;
        var confirmationTolerance = _volumeStep / 2m;
        EnqueueCommand(async () =>
        {
            var isCurrent = await Dispatcher.InvokeAsync(() => revision == _volumeRevision);
            if (!isCurrent)
            {
                return;
            }

            try
            {
                await _deviceManager.SetMainVolumeAsync(targetVolume).ConfigureAwait(false);
                var snapshot = await ConfirmDeviceStateAsync(snapshot =>
                        snapshot.MainZone?.Volume is decimal actualVolume &&
                        Math.Abs(actualVolume - targetVolume) <= confirmationTolerance)
                    .ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (revision != _volumeRevision)
                    {
                        return;
                    }

                    _pendingVolume = null;
                    UpdateSnapshot(snapshot);
                });
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() => FailPendingOperation(
                    revision == _volumeRevision,
                    () => _pendingVolume = null,
                    exception));
            }
        });
    }

    private void UpdateVolumeVisual()
    {
        var span = _volumeMaximum - _volumeMinimum;
        var ratio = span <= 0 ? 0d : (double)((_displayedVolume - _volumeMinimum) / span);
        ratio = Math.Clamp(ratio, 0d, 1d);
        var endAngle = -135d + ratio * 270d;
        var dialSize = VolumeDial.ActualWidth > 0 ? VolumeDial.ActualWidth : VolumeDial.Width;
        var center = dialSize / 2d;
        VolumeIndicatorLine.X1 = center;
        VolumeIndicatorLine.X2 = center;
        VolumeIndicatorLine.RenderTransform = new RotateTransform(endAngle, center, center);

        if (ratio <= 0.001)
        {
            VolumeArc.Data = Geometry.Empty;
        }
        else
        {
            var radius = Math.Max(0d, center - 12d);
            var start = PointOnCircle(center, radius, -135d);
            var end = PointOnCircle(center, radius, endAngle);
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
                ratio > 0.5, SweepDirection.Clockwise, true));
            VolumeArc.Data = new PathGeometry([figure]);
        }

        var format = _volumeStep == decimal.Truncate(_volumeStep) ? "0" : "0.#";
        VolumeValueText.Text = _displayedVolume.ToString(format, CultureInfo.CurrentCulture);
    }

    private void UpdateActualVolumeVisual(MainZoneStatusResponse? status)
    {
        if (status?.ActualVolume?.Value is not decimal actualVolume)
        {
            VolumeDbText.Text = "— dB";
            return;
        }

        var unit = string.IsNullOrWhiteSpace(status.ActualVolume.Unit)
            ? "dB"
            : status.ActualVolume.Unit;
        VolumeDbText.Text = $"{actualVolume.ToString("0.###", CultureInfo.CurrentCulture)} {unit}";
    }

    private static Point PointOnCircle(double center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180d;
        return new Point(center + radius * Math.Sin(radians), center - radius * Math.Cos(radians));
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var revision = ++_refreshRevision;
        _refreshInProgress = true;
        UpdateSnapshot(_deviceManager.Snapshot);
        EnqueueCommand(async () =>
        {
            try
            {
                await _deviceManager.RefreshAsync().ConfigureAwait(false);
                var snapshot = _deviceManager.Snapshot;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (revision != _refreshRevision)
                    {
                        return;
                    }

                    _refreshInProgress = false;
                    UpdateSnapshot(snapshot);
                });
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() => FailPendingOperation(
                    revision == _refreshRevision,
                    () => _refreshInProgress = false,
                    exception));
            }
        });
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _settingsWindow.ShowFromTray();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyPendingStatusVisual()
    {
        var text = GetOptimisticStatusText();
        if (text is null)
        {
            return;
        }

        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(101, 230, 209));
    }

    private void EnqueueDeviceChange(
        Func<Task> changeDeviceState,
        Func<DeviceSnapshot, bool> targetReached,
        Func<bool> isCurrent,
        Action clearPendingState)
    {
        EnqueueCommand(async () =>
        {
            try
            {
                await changeDeviceState().ConfigureAwait(false);
                var snapshot = await ConfirmDeviceStateAsync(targetReached).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!isCurrent())
                    {
                        return;
                    }

                    clearPendingState();
                    UpdateSnapshot(snapshot);
                });
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() => FailPendingOperation(
                    isCurrent(),
                    clearPendingState,
                    exception));
            }
        });
    }

    private async Task<DeviceSnapshot> ConfirmDeviceStateAsync(Func<DeviceSnapshot, bool> targetReached)
    {
        // The receiver can acknowledge a command before its status endpoint reflects the new value.
        // Keep the optimistic UI intact while giving the device time to settle, then confirm it.
        var confirmationDelays = new[]
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
            foreach (var delay in confirmationDelays)
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

    private void EnqueueCommand(Func<Task> command)
    {
        lock (_commandQueueSync)
        {
            _commandQueue = _commandQueue.ContinueWith(
                    async _ => await command().ConfigureAwait(false),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private void FailPendingOperation(bool isCurrent, Action clearPendingState, Exception exception)
    {
        if (!isCurrent)
        {
            return;
        }

        clearPendingState();
        UpdateSnapshot(_deviceManager.Snapshot);
        ShowOperationError(exception);
    }

    private static void ShowOperationError(Exception exception)
    {
        MessageBox.Show(exception switch
        {
            DeviceUnavailableException => "対応アンプに接続されていません。",
            CapabilityNotSupportedException => "この機器では利用できない操作です。",
            TimeoutException => "実機の状態確認がタイムアウトしました。現在の状態に表示を戻します。",
            OperationCanceledException => "操作がタイムアウトしました。",
            _ => "操作を完了できませんでした。ログを確認してください。"
        }, "NAVA", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _deviceManager.Settings.MinimizeToTray)
        {
            ShowInTaskbar = false;
            Hide();
        }
        else if (WindowState == WindowState.Minimized)
        {
            ShowInTaskbar = true;
        }
    }
}
