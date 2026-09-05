using System.Text.Json;
using System.Text.Json.Serialization;

namespace RxV4A.Host;

public sealed class AppSettings
{
    [JsonIgnore]
    public Lock SyncRoot { get; } = new();

    public string? ManualHost { get; set; }

    public string? LastDiscoveredHost { get; set; }

    public string? DiscoveryNetworkInterfaceId { get; set; }

    public CompactWindowPlacementSettings CompactWindow { get; set; } = new();

    public bool ShowCompactPowerStatus { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public string? PreferredDeviceId { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 4;

    public int ReconnectIntervalSeconds { get; set; } = 8;

    public int PollIntervalSeconds { get; set; } = 15;

    public LocalApiSettings LocalApi { get; set; } = new();

    public Dictionary<string, ActivityConfiguration> Activities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> PowerOnBlockers { get; set; } = [];

    public Dictionary<string, ActionSequenceBinding> ActionBindings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<GlobalHotkeyBinding> GlobalHotkeys { get; set; } = GlobalHotkeyDefaults.Create();
}

public sealed class CompactWindowPlacementSettings
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public double? VirtualScreenLeft { get; set; }

    public double? VirtualScreenTop { get; set; }

    public double? VirtualScreenWidth { get; set; }

    public double? VirtualScreenHeight { get; set; }
}

public sealed class ActivityConfiguration
{
    public string? InputId { get; set; }

    public int? SceneNumber { get; set; }

    public int StartupDelaySeconds { get; set; } = 3;
}

public sealed class LocalApiSettings
{
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 55274;
}

public sealed class HotkeyGesture
{
    public bool Ctrl { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    public int VirtualKey { get; set; }

    public string DisplayName =>
        string.Join("+", new[]
        {
            Ctrl ? "Ctrl" : null,
            Alt ? "Alt" : null,
            Shift ? "Shift" : null,
            VirtualKeyName(VirtualKey)
        }.Where(item => item is not null));

    public string Identity => $"{Ctrl}:{Alt}:{Shift}:{VirtualKey}";

    public static bool IsSupportedVirtualKey(int virtualKey) => virtualKey is
        >= 0x30 and <= 0x39 or // 0-9
        >= 0x41 and <= 0x5A or // A-Z
        >= 0x60 and <= 0x69 or // numeric keypad
        >= 0x70 and <= 0x87 or // F1-F24
        >= 0x21 and <= 0x28 or // navigation keys
        0x2D or 0x2E or       // Insert / Delete
        0x6A or 0x6B or 0x6D or 0x6E or 0x6F or // numeric keypad operators
        >= 0xAD and <= 0xB3;  // media keys

    public static string VirtualKeyName(int virtualKey) => virtualKey switch
    {
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x60 and <= 0x69 => $"Num {virtualKey - 0x60}",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Insert",
        0x2E => "Delete",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0xAD => "Media Mute",
        0xAE => "Media Volume Down",
        0xAF => "Media Volume Up",
        0xB0 => "Media Next",
        0xB1 => "Media Previous",
        0xB2 => "Media Stop",
        0xB3 => "Media Play/Pause",
        _ => $"VK 0x{virtualKey:X2}"
    };
}

public sealed class ActionSequenceBinding
{
    public List<string> OnActivate { get; set; } = [];

    public List<string> OnDeactivate { get; set; } = [];
}

public sealed class GlobalHotkeyBinding
{
    public HotkeyGesture Gesture { get; set; } = new();

    public string? ActionId { get; set; }

    public decimal? Amount { get; set; }
}

public static class HotkeyActionIds
{
    public const string PowerOn = "power-on";
    public const string PowerStandby = "power-standby";
    public const string PowerToggle = "power-toggle";
    public const string MuteOn = "mute-on";
    public const string MuteOff = "mute-off";
    public const string MuteToggle = "mute-toggle";
    public const string VolumeUp = "volume-up";
    public const string VolumeDown = "volume-down";
    public static string ActivateActivity(string activityId) => $"activity:{activityId}";

    public static string ToggleBlocker(string blockerId) => $"blocker:{blockerId}";

    public static string RegisteredAction(string actionId) => $"action:{actionId}";

    public static string SelectInput(string inputId) => $"input:{inputId}";

    public static string SelectSoundProgram(string programId) => $"sound-program:{programId}";

    public static bool RequiresAmount(string? actionId) =>
        string.Equals(actionId, VolumeUp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(actionId, VolumeDown, StringComparison.OrdinalIgnoreCase);
}

public static class GlobalHotkeyDefaults
{
    public static List<GlobalHotkeyBinding> Create() =>
    [
        Create(0x7C, HotkeyActionIds.PowerToggle),
        Create(0x7D, HotkeyActionIds.MuteToggle),
        Create(0x7E, HotkeyActionIds.VolumeDown, 1m),
        Create(0x7F, HotkeyActionIds.VolumeUp, 1m)
    ];

    public static bool IsLegacyDefault(IReadOnlyList<GlobalHotkeyBinding> bindings)
    {
        if (bindings.Count != 12)
        {
            return false;
        }

        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            if (!binding.Gesture.Ctrl || !binding.Gesture.Alt || binding.Gesture.Shift ||
                binding.Gesture.VirtualKey != 0x7C + index)
            {
                return false;
            }

            var expectedAction = index switch
            {
                0 => HotkeyActionIds.PowerOn,
                1 => HotkeyActionIds.PowerStandby,
                _ => null
            };
            if (!string.Equals(binding.ActionId, expectedAction, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static GlobalHotkeyBinding Create(int virtualKey, string? actionId, decimal? amount = null) => new()
    {
        Gesture = new HotkeyGesture { Ctrl = true, Alt = true, VirtualKey = virtualKey },
        ActionId = actionId,
        Amount = amount
    };
}

public interface ISettingsStore
{
    string SettingsPath { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly IReadOnlyList<string> _legacySettingsPaths;

    public JsonSettingsStore(string? localAppDataPath = null)
    {
        var root = localAppDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SettingsPath = Path.Combine(root, "NAVA", "settings.json");
        _legacySettingsPaths =
        [
            Path.Combine(root, "Network AV Amp Controller", "settings.json"),
            Path.Combine(root, "Yamaha AV Manager", "settings.json")
        ];
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loadPath = File.Exists(SettingsPath)
            ? SettingsPath
            : _legacySettingsPaths.FirstOrDefault(File.Exists);
        if (loadPath is null)
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(loadPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new AppSettings();
            settings.LocalApi ??= new LocalApiSettings();
            settings.CompactWindow ??= new CompactWindowPlacementSettings();
            settings.Activities = new Dictionary<string, ActivityConfiguration>(
                settings.Activities ?? [],
                StringComparer.OrdinalIgnoreCase);
            settings.PowerOnBlockers ??= [];
            settings.ActionBindings = new Dictionary<string, ActionSequenceBinding>(
                settings.ActionBindings ?? [],
                StringComparer.OrdinalIgnoreCase);

            settings.GlobalHotkeys ??= GlobalHotkeyDefaults.Create();
            var migratedHotkeys = GlobalHotkeyDefaults.IsLegacyDefault(settings.GlobalHotkeys);
            if (migratedHotkeys)
            {
                settings.GlobalHotkeys = GlobalHotkeyDefaults.Create();
            }
            SettingsValidator.Validate(settings);

            var loadedFromLegacyPath = _legacySettingsPaths.Contains(loadPath, StringComparer.OrdinalIgnoreCase);
            if (migratedHotkeys || loadedFromLegacyPath)
            {
                await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            return settings;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or
                                           UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)
                ?? throw new InvalidOperationException("The settings path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = SettingsPath + ".tmp";
            byte[] json;
            lock (settings.SyncRoot)
            {
                json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            }

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SettingsPath, true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
