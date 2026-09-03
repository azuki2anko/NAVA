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
    private readonly IDeviceManager _deviceManager;
    private readonly IControlOrchestrator _orchestrator;
    private readonly SettingsWindow _settingsWindow;
    private bool _allowClose;
    private bool _updatingControls;
    private bool _volumeDragging;
    private bool _operationInProgress;
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
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void AllowClose()
    {
        _allowClose = true;
        _deviceManager.SnapshotChanged -= DeviceManager_SnapshotChanged;
        _orchestrator.StateChanged -= Orchestrator_StateChanged;
    }

    public async Task ExecutePowerAsync(MainPower power) => await SetPowerAsync(power);

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

            ConnectionText.Text = snapshot.ConnectionState switch
            {
                DeviceConnectionState.Connected => "接続済み",
                DeviceConnectionState.Connecting => "接続中…",
                DeviceConnectionState.Reconnecting => "再接続中…",
                DeviceConnectionState.Unsupported => "未対応の機器",
                _ => "未接続"
            };
            DeviceText.Text = capabilities is null
                ? SnapshotMessage(snapshot)
                : $"{capabilities.DeviceInfo.ModelName}  •  API {capabilities.DeviceInfo.ApiVersion}";
            ConnectionIndicator.Fill = connected
                ? new SolidColorBrush(Color.FromRgb(101, 230, 209))
                : snapshot.ConnectionState is DeviceConnectionState.Connecting or DeviceConnectionState.Reconnecting
                    ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
                    : new SolidColorBrush(Color.FromRgb(100, 116, 139));

            var powerOn = string.Equals(status?.Power, "on", StringComparison.OrdinalIgnoreCase);
            PowerToggle.IsChecked = powerOn;
            PowerStateText.Text = powerOn ? "POWER ON" : "STANDBY";
            PowerStateText.Foreground = powerOn
                ? new SolidColorBrush(Color.FromRgb(101, 230, 209))
                : Brushes.WhiteSmoke;
            PowerToggle.IsEnabled = !_operationInProgress && connected &&
                                    capabilities?.SupportsZoneFunction("main", "power") == true;

            var hasMute = capabilities?.SupportsZoneFunction("main", "mute") == true;
            MuteToggle.Visibility = hasMute ? Visibility.Visible : Visibility.Collapsed;
            MuteToggle.IsChecked = status?.Mute == true;
            MuteToggle.IsEnabled = !_operationInProgress && connected;
            MuteStateText.Text = status?.Mute == true ? "ON" : "OFF";

            var inputs = zone?.Inputs.Select(input => input.Id)
                .Where(ApplicationScope.IsOperationalInput).ToArray() ?? [];
            SourceList.ItemsSource = inputs;
            SourceList.SelectedItem = inputs.FirstOrDefault(input =>
                string.Equals(input, status?.Input, StringComparison.OrdinalIgnoreCase));
            SourceText.Text = status?.Input ?? "—";
            SourceButton.IsEnabled = !_operationInProgress && connected && inputs.Length > 0;

            var programs = zone?.SoundPrograms.ToArray() ?? [];
            SoundFieldList.ItemsSource = programs;
            SoundFieldList.SelectedItem = programs.FirstOrDefault(program =>
                string.Equals(program, status?.SoundProgram, StringComparison.OrdinalIgnoreCase));
            SoundFieldText.Text = status?.SoundProgram ?? "—";
            SoundFieldButton.IsEnabled = !_operationInProgress && connected && programs.Length > 0;

            var range = capabilities?.FindZoneRange("main", "volume");
            var hasVolume = capabilities?.SupportsZoneFunction("main", "volume") == true &&
                            range?.Minimum is decimal && range.Maximum is decimal && range.Step is > 0;
            if (hasVolume)
            {
                _volumeMinimum = range!.Minimum!.Value;
                _volumeMaximum = range.Maximum!.Value;
                _volumeStep = range.Step!.Value;
            }

            if (status?.Volume is decimal volume && !_volumeDragging)
            {
                _displayedVolume = Math.Clamp(volume, _volumeMinimum, _volumeMaximum);
            }

            VolumeDial.IsEnabled = !_operationInProgress && connected && hasVolume;
            VolumeDownButton.IsEnabled = VolumeDial.IsEnabled;
            VolumeUpButton.IsEnabled = VolumeDial.IsEnabled;
            VolumeDial.Opacity = hasVolume ? 1 : 0.38;
            UpdateVolumeVisual();
            if (status?.Volume is not decimal)
            {
                VolumeValueText.Text = "—";
                VolumeUnitText.Text = string.Empty;
            }
            else
            {
                VolumeUnitText.Text = string.IsNullOrWhiteSpace(status.ActualVolume?.Unit)
                    ? "dB"
                    : status.ActualVolume.Unit;
            }

            var blockers = _orchestrator.ActiveBlockers;
            StatusText.Text = blockers.Count > 0
                ? $"電源ON禁止中  •  {string.Join(", ", blockers)}"
                : connected
                    ? $"最終更新  {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}"
                    : SnapshotMessage(snapshot);
            StatusText.Foreground = blockers.Count > 0
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

    private async void PowerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls)
        {
            return;
        }

        await SetPowerAsync(PowerToggle.IsChecked == true ? MainPower.On : MainPower.Standby);
    }

    private async Task SetPowerAsync(MainPower power)
    {
        await RunOperationAsync(async () =>
        {
            var result = await _orchestrator.SetPowerAsync(power, allowBlockedOn: false);
            if (result.Outcome is ControlOutcome.Blocked or ControlOutcome.PartialFailure or ControlOutcome.Failed)
            {
                MessageBox.Show(result.Message, "Yamaha AV Manager", MessageBoxButton.OK,
                    result.Outcome == ControlOutcome.Blocked ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        });
    }

    private async void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_updatingControls)
        {
            await RunOperationAsync(() => _deviceManager.SetMainMuteAsync(MuteToggle.IsChecked == true));
        }
    }

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        SourcePopup.IsOpen = !SourcePopup.IsOpen;
        if (SourceList.SelectedItem is not null)
        {
            SourceList.ScrollIntoView(SourceList.SelectedItem);
        }
    }

    private async void SourceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingControls || !SourcePopup.IsOpen || SourceList.SelectedItem is not string input)
        {
            return;
        }

        SourcePopup.IsOpen = false;
        await RunOperationAsync(() => _deviceManager.SetMainInputAsync(input));
    }

    private void SoundFieldButton_Click(object sender, RoutedEventArgs e)
    {
        SoundFieldPopup.IsOpen = !SoundFieldPopup.IsOpen;
        if (SoundFieldList.SelectedItem is not null)
        {
            SoundFieldList.ScrollIntoView(SoundFieldList.SelectedItem);
        }
    }

    private async void SoundFieldList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingControls || !SoundFieldPopup.IsOpen || SoundFieldList.SelectedItem is not string program)
        {
            return;
        }

        SoundFieldPopup.IsOpen = false;
        await RunOperationAsync(() => _deviceManager.SetMainSoundProgramAsync(program));
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

    private async void VolumeDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volumeDragging)
        {
            return;
        }

        SetVolumeFromPoint(e.GetPosition(VolumeDial));
        _volumeDragging = false;
        VolumeDial.ReleaseMouseCapture();
        await CommitVolumeAsync();
    }

    private async void VolumeDial_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!VolumeDial.IsEnabled)
        {
            return;
        }

        ChangeVolume(e.Delta > 0 ? _volumeStep : -_volumeStep);
        await CommitVolumeAsync();
        e.Handled = true;
    }

    private async void VolumeDownButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeVolume(-_volumeStep);
        await CommitVolumeAsync();
    }

    private async void VolumeUpButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeVolume(_volumeStep);
        await CommitVolumeAsync();
    }

    private void ChangeVolume(decimal delta)
    {
        _displayedVolume = SnapVolume(_displayedVolume + delta);
        UpdateVolumeVisual();
    }

    private void SetVolumeFromPoint(Point point)
    {
        var center = VolumeDial.ActualWidth / 2d;
        var angle = Math.Atan2(point.X - center, center - point.Y) * 180d / Math.PI;
        angle = Math.Clamp(angle, -135d, 135d);
        var ratio = (angle + 135d) / 270d;
        _displayedVolume = SnapVolume(_volumeMinimum + (decimal)ratio * (_volumeMaximum - _volumeMinimum));
        UpdateVolumeVisual();
    }

    private decimal SnapVolume(decimal value)
    {
        var steps = Math.Round((value - _volumeMinimum) / _volumeStep, MidpointRounding.AwayFromZero);
        return Math.Clamp(_volumeMinimum + steps * _volumeStep, _volumeMinimum, _volumeMaximum);
    }

    private async Task CommitVolumeAsync() =>
        await RunOperationAsync(() => _deviceManager.SetMainVolumeAsync(_displayedVolume));

    private void UpdateVolumeVisual()
    {
        var span = _volumeMaximum - _volumeMinimum;
        var ratio = span <= 0 ? 0d : (double)((_displayedVolume - _volumeMinimum) / span);
        ratio = Math.Clamp(ratio, 0d, 1d);
        var endAngle = -135d + ratio * 270d;
        VolumeIndicatorLine.RenderTransform = new RotateTransform(endAngle, 143, 143);

        if (ratio <= 0.001)
        {
            VolumeArc.Data = Geometry.Empty;
        }
        else
        {
            const double center = 143d;
            const double radius = 128d;
            var start = PointOnCircle(center, radius, -135d);
            var end = PointOnCircle(center, radius, endAngle);
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
                ratio > 0.5, SweepDirection.Clockwise, true));
            VolumeArc.Data = new PathGeometry([figure]);
        }

        VolumeValueText.Text = _displayedVolume.ToString("0.0", CultureInfo.CurrentCulture);
    }

    private static Point PointOnCircle(double center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180d;
        return new Point(center + radius * Math.Sin(radians), center - radius * Math.Cos(radians));
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(() => _deviceManager.RefreshAsync());

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _settingsWindow.ShowFromTray();

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UpdateSnapshot(_deviceManager.Snapshot);
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception switch
            {
                DeviceUnavailableException => "対応アンプに接続されていません。",
                CapabilityNotSupportedException => "この機器では利用できない操作です。",
                OperationCanceledException => "操作がタイムアウトしました。",
                _ => "操作を完了できませんでした。ログを確認してください。"
            }, "Yamaha AV Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _operationInProgress = false;
            UpdateSnapshot(_deviceManager.Snapshot);
        }
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
        if (WindowState == WindowState.Minimized)
        {
            ShowInTaskbar = false;
            Hide();
        }
    }
}
