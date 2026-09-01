using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using RxV4A.Host;

namespace RxV4A.Desktop;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly GlobalHotkeyService _globalHotkeys;
    private bool _capturing;

    public SettingsWindow(
        AppSettings settings,
        ISettingsStore settingsStore,
        GlobalHotkeyService globalHotkeys)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _globalHotkeys = globalHotkeys;
        LoadRows();
        DataContext = this;
        InitializeComponent();
    }

    public ObservableCollection<GlobalHotkeyRow> GlobalHotkeys { get; } = [];

    private void LoadRows()
    {
        lock (_settings.SyncRoot)
        {
            foreach (var binding in _settings.GlobalHotkeys)
            {
                GlobalHotkeys.Add(new GlobalHotkeyRow
                {
                    FunctionKey = $"F{binding.Gesture.VirtualKey - 0x6F}",
                    Ctrl = binding.Gesture.Ctrl,
                    Alt = binding.Gesture.Alt,
                    Shift = binding.Gesture.Shift,
                    ActionId = binding.ActionId ?? string.Empty
                });
            }
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalHotkeysGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        GlobalHotkeysGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        List<GlobalHotkeyBinding>? previous = null;
        try
        {
            var hotkeys = GlobalHotkeys.Select(ToBinding).ToList();
            lock (_settings.SyncRoot)
            {
                previous = _settings.GlobalHotkeys;
                _settings.GlobalHotkeys = hotkeys;
                SettingsValidator.Validate(_settings);
            }

            await _settingsStore.SaveAsync(_settings);

            _globalHotkeys.Reload();
            System.Windows.MessageBox.Show(
                "設定を保存し、グローバルホットキーを再登録しました。",
                "RX-V4A Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or IOException or
                                           UnauthorizedAccessException)
        {
            if (previous is not null)
            {
                lock (_settings.SyncRoot)
                {
                    _settings.GlobalHotkeys = previous;
                }
            }

            System.Windows.MessageBox.Show(
                $"設定を保存できません。\n\n{exception.Message}",
                "RX-V4A Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CaptureGlobalButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalHotkeysGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        GlobalHotkeysGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        if (GlobalHotkeysGrid.SelectedItem is not GlobalHotkeyRow)
        {
            CaptureStatusText.Text = "先にホットキー行を選択してください。";
            return;
        }

        _capturing = true;
        _globalHotkeys.Suspend();
        Activate();
        Focus();
        CaptureStatusText.Text = "F13～F24と修飾キーを押してください。Escで中止します。";
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndCapture("キー取得を中止しました。");
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is not (>= 0x7C and <= 0x87))
        {
            CaptureStatusText.Text = "入力ホットキーの主キーはF13～F24だけです。";
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var gesture = new HotkeyGesture
        {
            Ctrl = modifiers.HasFlag(ModifierKeys.Control),
            Alt = modifiers.HasFlag(ModifierKeys.Alt),
            Shift = modifiers.HasFlag(ModifierKeys.Shift),
            VirtualKey = virtualKey
        };
        if (GlobalHotkeysGrid.SelectedItem is GlobalHotkeyRow hotkey)
        {
            hotkey.FunctionKey = $"F{virtualKey - 0x6F}";
            hotkey.Ctrl = gesture.Ctrl;
            hotkey.Alt = gesture.Alt;
            hotkey.Shift = gesture.Shift;
        }

        EndCapture($"取得: {gesture.DisplayName}（保存すると反映されます）");
    }

    private void EndCapture(string message)
    {
        _capturing = false;
        CaptureStatusText.Text = message;
        _globalHotkeys.Reload();
    }

    private static GlobalHotkeyBinding ToBinding(GlobalHotkeyRow row)
    {
        if (!row.FunctionKey.StartsWith('F') ||
            !int.TryParse(row.FunctionKey.AsSpan(1), out var number) ||
            number is < 13 or > 24)
        {
            throw new InvalidDataException("グローバルホットキーの主キーはF13～F24で指定してください。");
        }

        return new GlobalHotkeyBinding
        {
            Gesture = new HotkeyGesture
            {
                Ctrl = row.Ctrl,
                Alt = row.Alt,
                Shift = row.Shift,
                VirtualKey = 0x6F + number
            },
            ActionId = string.IsNullOrWhiteSpace(row.ActionId) ? null : row.ActionId.Trim()
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_capturing)
        {
            _capturing = false;
            _globalHotkeys.Reload();
        }
    }
}

public sealed class GlobalHotkeyRow : ObservableRow
{
    private string _functionKey = "F13";
    private bool _ctrl;
    private bool _alt;
    private bool _shift;
    private string _actionId = string.Empty;

    public string FunctionKey { get => _functionKey; set => Set(ref _functionKey, value); }
    public bool Ctrl { get => _ctrl; set => Set(ref _ctrl, value); }
    public bool Alt { get => _alt; set => Set(ref _alt, value); }
    public bool Shift { get => _shift; set => Set(ref _shift, value); }
    public string ActionId { get => _actionId; set => Set(ref _actionId, value); }
}

public abstract class ObservableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
