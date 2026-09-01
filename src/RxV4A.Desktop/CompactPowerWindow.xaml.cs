using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RxV4A.Core;
using RxV4A.Host;
using MediaBrushes = System.Windows.Media.Brushes;

namespace RxV4A.Desktop;

public partial class CompactPowerWindow : Window
{
    private readonly IDeviceManager _deviceManager;
    private readonly IControlOrchestrator _orchestrator;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private HwndSource? _windowSource;
    private bool _allowClose;
    private bool _positionInitialized;
    private bool _operationInProgress;
    private DisplayGeometry? _lastDisplayGeometry;

    public CompactPowerWindow(
        IDeviceManager deviceManager,
        IControlOrchestrator orchestrator,
        AppSettings settings,
        ISettingsStore settingsStore)
    {
        _deviceManager = deviceManager;
        _orchestrator = orchestrator;
        _settings = settings;
        _settingsStore = settingsStore;
        InitializeComponent();
        ApplyStatusVisibility();
        SourceInitialized += Window_SourceInitialized;
        _deviceManager.SnapshotChanged += DeviceManager_SnapshotChanged;
        _orchestrator.StateChanged += Orchestrator_StateChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        UpdateState(_deviceManager.Snapshot);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
    }

    public void AllowClose() => _allowClose = true;

    private void ApplyStatusVisibility()
    {
        var showStatus = _settings.ShowCompactPowerStatus;
        PowerStatePanel.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        StateColumn.Width = new GridLength(showStatus ? 14 : 0);
    }

    private async void PowerToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var target = string.Equals(
            _deviceManager.Snapshot.MainZone?.Power,
            "on",
            StringComparison.OrdinalIgnoreCase)
            ? MainPower.Standby
            : MainPower.On;
        await SetPowerAsync(target);
    }

    private async void DragGrip_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            DragMove();
            await SavePlacementAsync();
        }
    }

    private async Task SetPowerAsync(MainPower power)
    {
        if (_operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UpdateButtons();
        try
        {
            var result = await _orchestrator.SetPowerAsync(power, allowBlockedOn: false);
            if (result.Outcome == ControlOutcome.Blocked)
            {
                StateText.Text = "ON禁止中";
                StateIndicator.Fill = MediaBrushes.DarkOrange;
                ToolTip = result.Message;
            }
            else if (result.Outcome is ControlOutcome.Failed or ControlOutcome.PartialFailure)
            {
                StateText.Text = "操作失敗";
                StateIndicator.Fill = MediaBrushes.IndianRed;
                ToolTip = result.Message;
            }
        }
        catch (Exception)
        {
            StateText.Text = "操作失敗";
            StateIndicator.Fill = MediaBrushes.IndianRed;
            ToolTip = "操作を完了できませんでした。ログを確認してください。";
        }
        finally
        {
            _operationInProgress = false;
            UpdateButtons();
        }
    }

    private void DeviceManager_SnapshotChanged(object? sender, DeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => UpdateState(snapshot));

    private void Orchestrator_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => UpdateState(_deviceManager.Snapshot));

    private void UpdateState(DeviceSnapshot snapshot)
    {
        ToolTip = null;
        if (snapshot.ConnectionState != DeviceConnectionState.Connected)
        {
            StateText.Text = "未接続";
            StateIndicator.Fill = MediaBrushes.Gray;
            PowerToggleButton.Tag = "Off";
        }
        else if (string.Equals(snapshot.MainZone?.Power, "on", StringComparison.OrdinalIgnoreCase))
        {
            StateText.Text = "ON";
            StateIndicator.Fill = MediaBrushes.LimeGreen;
            PowerToggleButton.Tag = "On";
        }
        else
        {
            StateText.Text = "OFF";
            StateIndicator.Fill = MediaBrushes.SlateGray;
            PowerToggleButton.Tag = "Off";
        }

        var blockers = _orchestrator.ActiveBlockers;
        if (blockers.Count > 0 && !string.Equals(snapshot.MainZone?.Power, "on", StringComparison.OrdinalIgnoreCase))
        {
            StateText.Text = "ON禁止中";
            StateIndicator.Fill = MediaBrushes.DarkOrange;
            ToolTip = $"有効なブロッカー: {string.Join(", ", blockers)}";
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var canOperate = !_operationInProgress &&
                         _deviceManager.Snapshot.ConnectionState == DeviceConnectionState.Connected &&
                         _deviceManager.Snapshot.Capabilities?.SupportsZoneFunction("main", "power") == true;
        PowerToggleButton.IsEnabled = canOperate;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_positionInitialized)
        {
            return;
        }

        _positionInitialized = true;
        var geometry = GetDisplayGeometry();
        _lastDisplayGeometry = geometry;
        var restored = false;
        lock (_settings.SyncRoot)
        {
            var placement = _settings.CompactWindow;
            ApplySavedSize(placement, geometry);
            var savedLeft = placement.Left;
            var savedTop = placement.Top;
            restored = MatchesDisplay(placement, geometry) &&
                       savedLeft.HasValue &&
                       savedTop.HasValue &&
                       IsVisibleOnDesktop(savedLeft.Value, savedTop.Value, geometry);
            if (restored)
            {
                Left = savedLeft!.Value;
                Top = savedTop!.Value;
            }
        }

        if (!restored)
        {
            ResetToDefaultPosition();
            await SavePlacementAsync();
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        HideSystemBorder(windowHandle);
    }

    private static void HideSystemBorder(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        const int dwmWindowAttributeBorderColor = 34;
        const uint dwmColorNone = 0xFFFFFFFE;
        var borderColor = dwmColorNone;
        _ = DwmSetWindowAttribute(
            windowHandle,
            dwmWindowAttributeBorderColor,
            ref borderColor,
            Marshal.SizeOf<uint>());
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int wmNcHitTest = 0x0084;
        const int wmExitSizeMove = 0x0232;
        if (message == wmExitSizeMove)
        {
            Dispatcher.BeginInvoke(async () => await SavePlacementAsync());
            return IntPtr.Zero;
        }

        if (message != wmNcHitTest || WindowState != WindowState.Normal)
        {
            return IntPtr.Zero;
        }

        var packed = lParam.ToInt64();
        var screenPoint = new System.Windows.Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
        var point = PointFromScreen(screenPoint);
        const double resizeBorder = 5;
        var left = point.X <= resizeBorder;
        var right = point.X >= ActualWidth - resizeBorder;
        var top = point.Y <= resizeBorder;
        var bottom = point.Y >= ActualHeight - resizeBorder;
        var hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => 13,    // HTTOPLEFT
            (_, true, true, _) => 14,    // HTTOPRIGHT
            (true, _, _, true) => 16,    // HTBOTTOMLEFT
            (_, true, _, true) => 17,    // HTBOTTOMRIGHT
            (true, _, _, _) => 10,       // HTLEFT
            (_, true, _, _) => 11,       // HTRIGHT
            (_, _, true, _) => 12,       // HTTOP
            (_, _, _, true) => 15,       // HTBOTTOM
            _ => 0
        };
        if (hit == 0)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(hit);
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            if (!IsLoaded)
            {
                return;
            }

            var geometry = GetDisplayGeometry();
            if (_lastDisplayGeometry is DisplayGeometry previousGeometry &&
                MatchesDisplay(previousGeometry, geometry))
            {
                return;
            }

            _lastDisplayGeometry = geometry;
            ClampSizeToDisplay(geometry);
            ResetToDefaultPosition();
            await SavePlacementAsync();
        });

    private void ResetToDefaultPosition()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 10;
        Top = workArea.Top + 10;
    }

    private async Task SavePlacementAsync()
    {
        if (!_positionInitialized)
        {
            return;
        }

        var geometry = GetDisplayGeometry();
        _lastDisplayGeometry = geometry;
        lock (_settings.SyncRoot)
        {
            _settings.CompactWindow.Left = Left;
            _settings.CompactWindow.Top = Top;
            _settings.CompactWindow.Width = ActualWidth;
            _settings.CompactWindow.Height = ActualHeight;
            _settings.CompactWindow.VirtualScreenLeft = geometry.Left;
            _settings.CompactWindow.VirtualScreenTop = geometry.Top;
            _settings.CompactWindow.VirtualScreenWidth = geometry.Width;
            _settings.CompactWindow.VirtualScreenHeight = geometry.Height;
        }

        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            // A placement-save failure must not stop receiver control.
        }
    }

    private bool IsVisibleOnDesktop(double left, double top, DisplayGeometry geometry) =>
        left < geometry.Left + geometry.Width &&
        left + ActualWidth > geometry.Left &&
        top < geometry.Top + geometry.Height &&
        top + ActualHeight > geometry.Top;

    private void ApplySavedSize(CompactWindowPlacementSettings placement, DisplayGeometry geometry)
    {
        if (placement.Width is double savedWidth && double.IsFinite(savedWidth))
        {
            Width = Math.Clamp(savedWidth, MinWidth, Math.Min(MaxWidth, geometry.Width));
        }

        if (placement.Height is double savedHeight && double.IsFinite(savedHeight))
        {
            Height = Math.Clamp(savedHeight, MinHeight, Math.Min(MaxHeight, geometry.Height));
        }
    }

    private void ClampSizeToDisplay(DisplayGeometry geometry)
    {
        Width = Math.Clamp(ActualWidth, MinWidth, Math.Min(MaxWidth, geometry.Width));
        Height = Math.Clamp(ActualHeight, MinHeight, Math.Min(MaxHeight, geometry.Height));
    }

    private static bool MatchesDisplay(
        CompactWindowPlacementSettings placement,
        DisplayGeometry geometry) =>
        AreClose(placement.VirtualScreenLeft, geometry.Left) &&
        AreClose(placement.VirtualScreenTop, geometry.Top) &&
        AreClose(placement.VirtualScreenWidth, geometry.Width) &&
        AreClose(placement.VirtualScreenHeight, geometry.Height);

    private static bool MatchesDisplay(DisplayGeometry previous, DisplayGeometry current) =>
        Math.Abs(previous.Left - current.Left) < 0.5 &&
        Math.Abs(previous.Top - current.Top) < 0.5 &&
        Math.Abs(previous.Width - current.Width) < 0.5 &&
        Math.Abs(previous.Height - current.Height) < 0.5;

    private static bool AreClose(double? saved, double current) =>
        saved.HasValue && Math.Abs(saved.Value - current) < 0.5;

    private static DisplayGeometry GetDisplayGeometry() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _deviceManager.SnapshotChanged -= DeviceManager_SnapshotChanged;
            _orchestrator.StateChanged -= Orchestrator_StateChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _windowSource?.RemoveHook(WindowMessageHook);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private readonly record struct DisplayGeometry(double Left, double Top, double Width, double Height);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);
}
