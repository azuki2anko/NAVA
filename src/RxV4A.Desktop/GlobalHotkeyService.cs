using System.Runtime.InteropServices;
using System.Windows.Interop;
using RxV4A.Host;

namespace RxV4A.Desktop;

public sealed record GlobalHotkeyRegistration(string Gesture, string ActionId, bool Registered, string? ErrorCode);

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const int FirstRegistrationId = 0x5100;

    private readonly AppSettings _settings;
    private readonly Func<string, Task> _executeAction;
    private readonly Dictionary<int, string> _actions = [];
    private HwndSource? _source;
    private nint _windowHandle;
    private bool _disposed;

    public GlobalHotkeyService(AppSettings settings, Func<string, Task> executeAction)
    {
        _settings = settings;
        _executeAction = executeAction;
    }

    public event EventHandler<IReadOnlyList<GlobalHotkeyRegistration>>? RegistrationsChanged;

    public IReadOnlyList<GlobalHotkeyRegistration> Registrations { get; private set; } = [];

    public void Initialize(nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not null)
        {
            throw new InvalidOperationException("The global hotkey service is already initialized.");
        }

        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("The WPF window source is not available.");
        _source.AddHook(WindowProcedure);
        Reload();
    }

    public void Reload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHandle == 0)
        {
            return;
        }

        UnregisterAll();
        List<GlobalHotkeyBinding> bindings;
        lock (_settings.SyncRoot)
        {
            SettingsValidator.ValidateGlobalHotkeys(_settings);
            bindings = _settings.GlobalHotkeys
                .Where(item => !string.IsNullOrWhiteSpace(item.ActionId))
                .Select(Clone)
                .ToList();
        }

        var results = new List<GlobalHotkeyRegistration>(bindings.Count);
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            var id = FirstRegistrationId + index;
            var registered = RegisterHotKey(
                _windowHandle,
                id,
                ToNativeModifiers(binding.Gesture),
                (uint)binding.Gesture.VirtualKey);
            if (registered)
            {
                _actions[id] = binding.ActionId!;
            }

            results.Add(new GlobalHotkeyRegistration(
                binding.Gesture.DisplayName,
                binding.ActionId!,
                registered,
                registered ? null : "hotkey_conflict"));
        }

        Registrations = results;
        RegistrationsChanged?.Invoke(this, Registrations);
    }

    public void Suspend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterAll();
        Registrations = [];
        RegistrationsChanged?.Invoke(this, Registrations);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterAll();
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = 0;
        _disposed = true;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var actionId))
        {
            handled = true;
            _ = _executeAction(actionId);
        }

        return 0;
    }

    private void UnregisterAll()
    {
        if (_windowHandle != 0)
        {
            foreach (var id in _actions.Keys)
            {
                UnregisterHotKey(_windowHandle, id);
            }
        }

        _actions.Clear();
    }

    private static uint ToNativeModifiers(HotkeyGesture gesture) => ModNoRepeat |
        (gesture.Ctrl ? ModControl : 0) |
        (gesture.Alt ? ModAlt : 0) |
        (gesture.Shift ? ModShift : 0);

    private static GlobalHotkeyBinding Clone(GlobalHotkeyBinding source) => new()
    {
        ActionId = source.ActionId,
        Gesture = new HotkeyGesture
        {
            Ctrl = source.Gesture.Ctrl,
            Alt = source.Gesture.Alt,
            Shift = source.Gesture.Shift,
            VirtualKey = source.Gesture.VirtualKey
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
