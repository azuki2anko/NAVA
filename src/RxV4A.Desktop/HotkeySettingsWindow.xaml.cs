using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using RxV4A.Core;
using RxV4A.Host;

namespace RxV4A.Desktop;

public partial class HotkeySettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly GlobalHotkeyService _globalHotkeys;
    private readonly IDeviceManager _deviceManager;
    private readonly IRegisteredActionService _registeredActions;
    private bool _capturing;

    public HotkeySettingsWindow(
        AppSettings settings,
        ISettingsStore settingsStore,
        GlobalHotkeyService globalHotkeys,
        IDeviceManager deviceManager,
        IRegisteredActionService registeredActions)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _globalHotkeys = globalHotkeys;
        _deviceManager = deviceManager;
        _registeredActions = registeredActions;
        KeyOptions = BuildKeyOptions();
        ActionOptions = BuildActionOptions();
        LoadRows();
        DataContext = this;
        InitializeComponent();
    }

    public ObservableCollection<GlobalHotkeyRow> GlobalHotkeys { get; } = [];

    public IReadOnlyList<HotkeyKeyOption> KeyOptions { get; }

    public IReadOnlyList<HotkeyActionOption> ActionOptions { get; }

    private void LoadRows()
    {
        lock (_settings.SyncRoot)
        {
            foreach (var binding in _settings.GlobalHotkeys)
            {
                GlobalHotkeys.Add(ToRow(binding));
            }
        }
    }

    private static GlobalHotkeyRow ToRow(GlobalHotkeyBinding binding) => new()
    {
        VirtualKey = binding.Gesture.VirtualKey,
        Ctrl = binding.Gesture.Ctrl,
        Alt = binding.Gesture.Alt,
        Shift = binding.Gesture.Shift,
        ActionId = binding.ActionId ?? string.Empty,
        AmountText = binding.Amount?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty
    };

    private void AddHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var usedKeys = GlobalHotkeys
            .Where(item => item.Ctrl && item.Alt && !item.Shift)
            .Select(item => item.VirtualKey)
            .ToHashSet();
        var virtualKey = KeyOptions.FirstOrDefault(option => !usedKeys.Contains(option.VirtualKey))?.VirtualKey
                         ?? KeyOptions[0].VirtualKey;
        var row = new GlobalHotkeyRow
        {
            VirtualKey = virtualKey,
            Ctrl = true,
            Alt = true
        };
        GlobalHotkeys.Add(row);
        GlobalHotkeysGrid.SelectedItem = row;
        GlobalHotkeysGrid.ScrollIntoView(row);
        CaptureStatusText.Text = "ホットキー行を追加しました。キーとアクションを選択してください。";
    }

    private void RemoveHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (GlobalHotkeysGrid.SelectedItem is not GlobalHotkeyRow row)
        {
            CaptureStatusText.Text = "削除する行を選択してください。";
            return;
        }

        GlobalHotkeys.Remove(row);
        CaptureStatusText.Text = "選択した行を削除しました。保存すると反映されます。";
    }

    private void ResetHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalHotkeys.Clear();
        foreach (var binding in GlobalHotkeyDefaults.Create())
        {
            GlobalHotkeys.Add(ToRow(binding));
        }

        CaptureStatusText.Text = "既定の4件へ戻しました。保存すると反映されます。";
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
                "NAVA",
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
                "NAVA",
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
        CaptureStatusText.Text = "対応する英数字、Fキー、移動キー、メディアキーを押してください。Escで中止します。";
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
        if (!HotkeyGesture.IsSupportedVirtualKey(virtualKey))
        {
            CaptureStatusText.Text = "このキーはグローバルホットキーに使用できません。";
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
            hotkey.VirtualKey = virtualKey;
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
        if (!HotkeyGesture.IsSupportedVirtualKey(row.VirtualKey))
        {
            throw new InvalidDataException("対応リストからホットキーを選択してください。");
        }

        var actionId = string.IsNullOrWhiteSpace(row.ActionId) ? null : row.ActionId.Trim();
        decimal? amount = null;
        if (HotkeyActionIds.RequiresAmount(actionId))
        {
            if ((!decimal.TryParse(row.AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) &&
                 !decimal.TryParse(row.AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)) ||
                parsed is <= 0 or > 100)
            {
                throw new InvalidDataException("音量の増減量は0より大きく100以下の数値で指定してください。");
            }

            amount = parsed;
        }

        return new GlobalHotkeyBinding
        {
            Gesture = new HotkeyGesture
            {
                Ctrl = row.Ctrl,
                Alt = row.Alt,
                Shift = row.Shift,
                VirtualKey = row.VirtualKey
            },
            ActionId = actionId,
            Amount = amount
        };
    }

    private IReadOnlyList<HotkeyActionOption> BuildActionOptions()
    {
        var options = new List<HotkeyActionOption>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, string displayName)
        {
            if (known.Add(id))
            {
                options.Add(new HotkeyActionOption(id, displayName));
            }
        }

        Add(string.Empty, "未割り当て");
        Add(HotkeyActionIds.PowerToggle, "電源：ON／スタンバイ切り替え");
        Add(HotkeyActionIds.PowerOn, "電源：ON");
        Add(HotkeyActionIds.PowerStandby, "電源：スタンバイ");
        Add(HotkeyActionIds.MuteToggle, "ミュート：切り替え");
        Add(HotkeyActionIds.MuteOn, "ミュート：ON");
        Add(HotkeyActionIds.MuteOff, "ミュート：OFF");
        Add(HotkeyActionIds.VolumeUp, "音量：上げる");
        Add(HotkeyActionIds.VolumeDown, "音量：下げる");

        var zone = _deviceManager.Snapshot.Capabilities?.FindZone("main");
        foreach (var input in zone?.Inputs.Where(item => ApplicationScope.IsOperationalInput(item.Id)) ?? [])
        {
            Add(HotkeyActionIds.SelectInput(input.Id), $"ソース：{input.Id}");
        }

        foreach (var program in zone?.SoundPrograms ?? [])
        {
            Add(HotkeyActionIds.SelectSoundProgram(program), $"音場：{program}");
        }

        lock (_settings.SyncRoot)
        {
            foreach (var activityId in _settings.Activities.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                Add(HotkeyActionIds.ActivateActivity(activityId), $"アクティビティ：{activityId}");
            }

            foreach (var blockerId in _settings.PowerOnBlockers.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                Add(HotkeyActionIds.ToggleBlocker(blockerId), $"電源ON禁止：{blockerId}を切り替え");
            }
        }

        foreach (var action in _registeredActions.GetRegisteredActions())
        {
            Add(HotkeyActionIds.RegisteredAction(action.Id), $"登録アクション：{action.DisplayName}");
        }

        lock (_settings.SyncRoot)
        {
            foreach (var savedActionId in _settings.GlobalHotkeys.Select(item => item.ActionId)
                         .Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                Add(savedActionId!, $"保存済み：{savedActionId}");
            }
        }

        return options;
    }

    private static IReadOnlyList<HotkeyKeyOption> BuildKeyOptions()
    {
        var virtualKeys = new List<int>();
        virtualKeys.AddRange(Enumerable.Range(0x70, 24));
        virtualKeys.AddRange(Enumerable.Range(0x41, 26));
        virtualKeys.AddRange(Enumerable.Range(0x30, 10));
        virtualKeys.AddRange(Enumerable.Range(0x60, 10));
        virtualKeys.AddRange([0x6A, 0x6B, 0x6D, 0x6E, 0x6F]);
        virtualKeys.AddRange([0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E]);
        virtualKeys.AddRange(Enumerable.Range(0xAD, 7));
        return virtualKeys.Select(key => new HotkeyKeyOption(key, HotkeyGesture.VirtualKeyName(key))).ToArray();
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
    private int _virtualKey = 0x7C;
    private bool _ctrl;
    private bool _alt;
    private bool _shift;
    private string _actionId = string.Empty;
    private string _amountText = string.Empty;

    public int VirtualKey { get => _virtualKey; set => Set(ref _virtualKey, value); }
    public bool Ctrl { get => _ctrl; set => Set(ref _ctrl, value); }
    public bool Alt { get => _alt; set => Set(ref _alt, value); }
    public bool Shift { get => _shift; set => Set(ref _shift, value); }
    public string ActionId
    {
        get => _actionId;
        set
        {
            if (!Set(ref _actionId, value))
            {
                return;
            }

            if (RequiresAmount && string.IsNullOrWhiteSpace(AmountText))
            {
                AmountText = "1";
            }

            RaisePropertyChanged(nameof(RequiresAmount));
        }
    }
    public string AmountText { get => _amountText; set => Set(ref _amountText, value); }
    public bool RequiresAmount => HotkeyActionIds.RequiresAmount(ActionId);
}

public sealed record HotkeyKeyOption(int VirtualKey, string DisplayName);

public sealed record HotkeyActionOption(string Id, string DisplayName);

public abstract class ObservableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    protected void RaisePropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
