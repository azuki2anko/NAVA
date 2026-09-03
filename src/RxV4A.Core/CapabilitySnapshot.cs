namespace RxV4A.Core;

public sealed record CapabilitySnapshot(
    DeviceInfoResponse DeviceInfo,
    FeaturesResponse Features,
    AdvancedFeaturesResponse AdvancedFeatures)
{
    public ZoneFeatures? FindZone(string zoneId) =>
        Features.Zones.FirstOrDefault(zone =>
            string.Equals(zone.Id, zoneId, StringComparison.OrdinalIgnoreCase));

    public bool SupportsZoneFunction(string zoneId, string function) =>
        FindZone(zoneId)?.Functions.Contains(function, StringComparer.OrdinalIgnoreCase) == true;

    public bool SupportsSystemFunction(string function) =>
        Features.System?.Functions.Contains(function, StringComparer.OrdinalIgnoreCase) == true;

    public RangeStepFeature? FindZoneRange(string zoneId, string rangeId) =>
        FindZone(zoneId)?.Ranges.FirstOrDefault(range =>
            string.Equals(range.Id, rangeId, StringComparison.OrdinalIgnoreCase));
}

public interface ICapabilityStore
{
    CapabilitySnapshot? Current { get; }

    void Replace(CapabilitySnapshot snapshot);

    void Clear();
}

public sealed class CapabilityStore : ICapabilityStore
{
    private CapabilitySnapshot? _current;

    public CapabilitySnapshot? Current => Volatile.Read(ref _current);

    public void Replace(CapabilitySnapshot snapshot) => Volatile.Write(ref _current, snapshot);

    public void Clear() => Volatile.Write(ref _current, null);
}
