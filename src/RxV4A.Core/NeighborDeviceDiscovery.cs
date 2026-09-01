using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RxV4A.Core;

public interface INeighborDeviceDiscovery
{
    IReadOnlyList<string> GetCandidateHosts(string? networkInterfaceId);
}

/// <summary>
/// Reads Windows' existing IPv4 neighbor cache through the fixed arp.exe command.
/// It sends no packets and intentionally never parses or exposes physical addresses.
/// </summary>
public sealed partial class WindowsNeighborDeviceDiscovery : INeighborDeviceDiscovery
{
    public IReadOnlyList<string> GetCandidateHosts(string? networkInterfaceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var sourceAddresses = GetSourceAddresses(networkInterfaceId);
        if (networkInterfaceId is not null && sourceAddresses.Count == 0)
        {
            return [];
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queries = networkInterfaceId is null
            ? new IPAddress?[] { null }
            : sourceAddresses.Cast<IPAddress?>();
        foreach (var sourceAddress in queries)
        {
            ReadArpOutput(sourceAddress, hosts);
        }

        return hosts.ToArray();
    }

    private static void ReadArpOutput(IPAddress? sourceAddress, HashSet<string> hosts)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "arp.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-a");
            if (sourceAddress is not null)
            {
                process.StartInfo.ArgumentList.Add("-N");
                process.StartInfo.ArgumentList.Add(sourceAddress.ToString());
            }

            if (!process.Start())
            {
                return;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return;
            }

            foreach (Match match in Ipv4AtLineStart().Matches(output))
            {
                if (IPAddress.TryParse(match.Groups[1].Value, out var address) && IsPrivate(address))
                {
                    hosts.Add(address.ToString());
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // An unavailable neighbor-cache provider is equivalent to no candidates.
        }
    }

    private static IReadOnlyList<IPAddress> GetSourceAddresses(string? networkInterfaceId)
    {
        if (networkInterfaceId is null)
        {
            return [];
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => string.Equals(
                    network.Id,
                    networkInterfaceId,
                    StringComparison.OrdinalIgnoreCase))
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address))
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
    }

    [GeneratedRegex(@"(?m)^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+")]
    private static partial Regex Ipv4AtLineStart();
}
