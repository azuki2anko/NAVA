using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RxV4A.Core;

public interface IDeviceDiscovery
{
    Task<IReadOnlyList<string>> DiscoverHostsAsync(TimeSpan duration, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> DiscoverHostsAsync(
        TimeSpan duration,
        string? networkInterfaceId,
        CancellationToken cancellationToken) => DiscoverHostsAsync(duration, cancellationToken);
}

public sealed class SsdpDeviceDiscovery : IDeviceDiscovery
{
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:MediaRenderer:1",
        "urn:schemas-upnp-org:device:MediaRenderer:2",
        "urn:schemas-yamaha-com:device:MediaRenderer:1"
    ];

    public async Task<IReadOnlyList<string>> DiscoverHostsAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
        => await DiscoverHostsAsync(duration, null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> DiscoverHostsAsync(
        TimeSpan duration,
        string? networkInterfaceId,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var localAddresses = GetActiveIpv4Addresses(networkInterfaceId);
        if (localAddresses.Count == 0)
        {
            return await DiscoverOnInterfaceAsync(null, duration, cancellationToken).ConfigureAwait(false);
        }

        var results = await Task.WhenAll(localAddresses.Select(address =>
            DiscoverOnInterfaceAsync(address, duration, cancellationToken))).ConfigureAwait(false);
        return results.SelectMany(item => item)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> DiscoverOnInterfaceAsync(
        IPAddress? localAddress,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        using var client = localAddress is null
            ? new UdpClient(AddressFamily.InterNetwork)
            : new UdpClient(new IPEndPoint(localAddress, 0));

        if (localAddress is not null)
        {
            client.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                localAddress.GetAddressBytes());
        }

        try
        {
            foreach (var searchTarget in SearchTargets)
            {
                var request = string.Join("\r\n",
                    "M-SEARCH * HTTP/1.1",
                    "HOST: 239.255.255.250:1900",
                    "MAN: \"ssdp:discover\"",
                    "MX: 2",
                    $"ST: {searchTarget}",
                    string.Empty,
                    string.Empty);
                var bytes = Encoding.ASCII.GetBytes(request);
                await client.SendAsync(bytes, SsdpEndpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SocketException)
        {
            return [];
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (true)
            {
                var response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (response.Buffer.Length > 0 && IsLocalAddress(response.RemoteEndPoint.Address))
                {
                    hosts.Add(response.RemoteEndPoint.Address.ToString());
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return hosts.ToArray();
        }
        catch (SocketException)
        {
            return hosts.ToArray();
        }
    }

    private static IReadOnlyList<IPAddress> GetActiveIpv4Addresses(string? networkInterfaceId)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network =>
                    network.OperationalStatus == OperationalStatus.Up &&
                    network.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    network.SupportsMulticast &&
                    (networkInterfaceId is null ||
                     string.Equals(network.Id, networkInterfaceId, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address =>
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address))
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254));
    }
}
