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

    private static string VirtualKeyName(int virtualKey) => virtualKey switch
    {
        >= 0x7C and <= 0x87 => $"F{virtualKey - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
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
}

public static class HotkeyActionIds
{
    public const string PowerOn = "power-on";
    public const string PowerStandby = "power-standby";
    public static string ActivateActivity(string activityId) => $"activity:{activityId}";

    public static string ToggleBlocker(string blockerId) => $"blocker:{blockerId}";

    public static string RegisteredAction(string actionId) => $"action:{actionId}";
}

public static class GlobalHotkeyDefaults
{
    public static List<GlobalHotkeyBinding> Create() =>
    [
        Create(0x7C, HotkeyActionIds.PowerOn),
        Create(0x7D, HotkeyActionIds.PowerStandby),
        Create(0x7E, null),
        Create(0x7F, null),
        Create(0x80, null),
        Create(0x81, null),
        Create(0x82, null),
        Create(0x83, null),
        Create(0x84, null),
        Create(0x85, null),
        Create(0x86, null),
        Create(0x87, null)
    ];

    private static GlobalHotkeyBinding Create(int virtualKey, string? actionId) => new()
    {
        Gesture = new HotkeyGesture { Ctrl = true, Alt = true, VirtualKey = virtualKey },
        ActionId = actionId
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

    public JsonSettingsStore(string? localAppDataPath = null)
    {
        var root = localAppDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SettingsPath = Path.Combine(root, "Yamaha AV Manager", "settings.json");
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
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
            SettingsValidator.Validate(settings);

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
