using RxV4A.Core;

namespace RxV4A.Host;

public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Unsupported
}

public sealed record DeviceSnapshot(
    DeviceConnectionState ConnectionState,
    CapabilitySnapshot? Capabilities,
    MainZoneStatusResponse? MainZone,
    DateTimeOffset UpdatedAt,
    string? ErrorCode = null,
    PingScanConfirmation? PingScanConfirmation = null)
{
    public static DeviceSnapshot Initial { get; } = new(
        DeviceConnectionState.Disconnected,
        null,
        null,
        DateTimeOffset.MinValue);
}

public sealed record PingScanConfirmation(long HostCount, int BroadestPrefixLength);

public sealed record PowerOperationResult(MainPower RequestedPower, DeviceSnapshot Snapshot);

public sealed class DeviceUnavailableException(string message) : Exception(message);

public interface IDeviceManager
{
    DeviceSnapshot Snapshot { get; }

    AppSettings Settings { get; }

    event EventHandler<DeviceSnapshot>? SnapshotChanged;

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<PowerOperationResult> SetMainPowerAsync(MainPower power, CancellationToken cancellationToken = default);

    Task<DeviceSnapshot> SetMainInputAsync(string inputId, CancellationToken cancellationToken = default);

    Task<DeviceSnapshot> RecallMainSceneAsync(int sceneNumber, CancellationToken cancellationToken = default);

    Task UpdateManualHostAsync(string? host, CancellationToken cancellationToken = default);

    IReadOnlyList<DiscoveryNetworkInterface> GetDiscoveryNetworkInterfaces();

    Task UpdateConnectionSettingsAsync(
        string? host,
        string? networkInterfaceId,
        CancellationToken cancellationToken = default);

    Task ApproveBroadSubnetPingScanAsync(CancellationToken cancellationToken = default);
}
