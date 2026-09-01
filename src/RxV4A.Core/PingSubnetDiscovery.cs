using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RxV4A.Core;

public sealed record PingSubnetDiscoveryResult(
    IReadOnlyList<string> Hosts,
    bool ConfirmationRequired,
    long ConfirmationHostCount,
    int? BroadestPrefixLength,
    bool TooLargeSubnetPresent,
    long ScannedHostCount = 0,
    int PingResponsiveCount = 0);

public interface IPingSubnetDiscovery
{
    IReadOnlyList<DiscoveryNetworkInterface> GetNetworkInterfaces();

    Task<PingSubnetDiscoveryResult> DiscoverHostsAsync(
        bool includeBroadSubnets,
        string? networkInterfaceId,
        CancellationToken cancellationToken);
}

public sealed record DiscoveryNetworkInterface(
    string Id,
    string Name,
    int PrefixLength,
    bool HasDefaultGateway,
    long HostCount);

public sealed class PingSubnetDiscovery : IPingSubnetDiscovery
{
    private const int AutomaticPrefixLength = 24;
    private const int MinimumScannablePrefixLength = 16;
    private const int PingTimeoutMilliseconds = 250;
    private const int TcpProbeTimeoutMilliseconds = 350;
    private const int MaximumConcurrency = 128;

    public IReadOnlyList<DiscoveryNetworkInterface> GetNetworkInterfaces()
    {
        try
        {
            return GetActiveInterfaceRanges(null)
                .GroupBy(item => item.InterfaceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new DiscoveryNetworkInterface(
                    group.Key,
                    group.First().InterfaceName,
                    group.Min(item => item.Range.PrefixLength),
                    group.Any(item => item.HasDefaultGateway),
                    group.Sum(item => item.Range.HostCount)))
                .OrderByDescending(item => item.HasDefaultGateway)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public async Task<PingSubnetDiscoveryResult> DiscoverHostsAsync(
        bool includeBroadSubnets,
        string? networkInterfaceId,
        CancellationToken cancellationToken)
    {
        var ranges = GetActiveInterfaceRanges(networkInterfaceId);
        var automatic = ranges.Where(item => item.Range.PrefixLength >= AutomaticPrefixLength).ToArray();
        var broad = ranges.Where(item => item.Range.PrefixLength is >= MinimumScannablePrefixLength and < AutomaticPrefixLength)
            .ToArray();
        var tooLarge = ranges.Any(item => item.Range.PrefixLength < MinimumScannablePrefixLength);
        var selected = includeBroadSubnets ? automatic.Concat(broad).ToArray() : automatic;
        var responsive = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var pingResponsiveCount = 0;

        await Parallel.ForEachAsync(
            selected.SelectMany(item => item.Range.EnumerateHostAddresses()
                    .Select(address => new ScanTarget(address, item.SourceAddress)))
                .Distinct(),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumConcurrency,
                CancellationToken = cancellationToken
            },
            async (target, token) =>
            {
                var pingTask = RespondsToPingAsync(target.Address, token);
                var httpTask = HasYamahaHttpPortAsync(target.Address, target.SourceAddress, token);
                if (await pingTask.ConfigureAwait(false))
                {
                    Interlocked.Increment(ref pingResponsiveCount);
                }

                if (await httpTask.ConfigureAwait(false))
                {
                    responsive.TryAdd(target.Address.ToString(), 0);
                }
            }).ConfigureAwait(false);

        return new PingSubnetDiscoveryResult(
            responsive.Keys.ToArray(),
            !includeBroadSubnets && broad.Length > 0,
            broad.Sum(item => item.Range.HostCount),
            broad.Length == 0 ? null : broad.Min(item => item.Range.PrefixLength),
            tooLarge,
            selected.Sum(item => item.Range.HostCount),
            pingResponsiveCount);
    }

    private static async Task<bool> RespondsToPingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, PingTimeoutMilliseconds)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception exception) when (exception is PingException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    private static async Task<bool> HasYamahaHttpPortAsync(
        IPAddress address,
        IPAddress sourceAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(TcpProbeTimeoutMilliseconds));
            using var client = new TcpClient(AddressFamily.InterNetwork);
            client.Client.Bind(new IPEndPoint(sourceAddress, 0));
            await client.ConnectAsync(address, 80, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    private static IReadOnlyList<InterfaceRange> GetActiveInterfaceRanges(string? networkInterfaceId)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network =>
                    network.OperationalStatus == OperationalStatus.Up &&
                    network.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    (networkInterfaceId is null ||
                     string.Equals(network.Id, networkInterfaceId, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(network =>
                {
                    var properties = network.GetIPProperties();
                    var hasGateway = properties.GatewayAddresses.Any(item =>
                        item.Address.AddressFamily == AddressFamily.InterNetwork);
                    return properties.UnicastAddresses
                        .Where(unicast =>
                            unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(unicast.Address) &&
                            IsPrivateOrLinkLocal(unicast.Address))
                        .Select(unicast => new InterfaceRange(
                            network.Id,
                            network.Name,
                            unicast.Address,
                            hasGateway,
                            PingSubnetRange.Create(unicast.Address, unicast.PrefixLength)));
                })
                .Where(item => item.Range.HostCount > 0)
                .DistinctBy(item => (item.InterfaceId, item.Range.NetworkValue, item.Range.PrefixLength))
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private sealed record InterfaceRange(
        string InterfaceId,
        string InterfaceName,
        IPAddress SourceAddress,
        bool HasDefaultGateway,
        PingSubnetRange Range);

    private sealed record ScanTarget(IPAddress Address, IPAddress SourceAddress);

    private static bool IsPrivateOrLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254));
    }
}

public sealed record PingSubnetRange(
    uint NetworkValue,
    int PrefixLength,
    uint FirstHostValue,
    uint LastHostValue,
    long HostCount)
{
    public static PingSubnetRange Create(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        var value = ToUInt32(address);
        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        var network = value & mask;
        var broadcast = network | ~mask;
        var first = prefixLength <= 30 ? network + 1 : network;
        var last = prefixLength <= 30 ? broadcast - 1 : broadcast;
        var count = last >= first ? (long)last - first + 1 : 0;
        return new PingSubnetRange(network, prefixLength, first, last, count);
    }

    public IEnumerable<IPAddress> EnumerateHostAddresses()
    {
        for (var value = FirstHostValue; value <= LastHostValue; value++)
        {
            yield return FromUInt32(value);
            if (value == uint.MaxValue)
            {
                yield break;
            }
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IPAddress FromUInt32(uint value) => new(
    [
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value
    ]);
}
