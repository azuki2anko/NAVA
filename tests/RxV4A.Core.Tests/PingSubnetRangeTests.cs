using System.Net;
using RxV4A.Core;

namespace RxV4A.Core.Tests;

public sealed class PingSubnetRangeTests
{
    [Theory]
    [InlineData(24, 254)]
    [InlineData(23, 510)]
    [InlineData(20, 4094)]
    [InlineData(16, 65534)]
    public void HostCount_IsCalculatedFromPrefixWithoutScanning(int prefixLength, long expectedHosts)
    {
        var range = PingSubnetRange.Create(IPAddress.Parse("192.0.2.25"), prefixLength);

        Assert.Equal(expectedHosts, range.HostCount);
    }

    [Fact]
    public void EnumeratedHosts_ExcludeNetworkAndBroadcastAddresses()
    {
        var range = PingSubnetRange.Create(IPAddress.Parse("192.0.2.25"), 30);

        var hosts = range.EnumerateHostAddresses().Select(item => item.ToString()).ToArray();

        Assert.Equal(["192.0.2.25", "192.0.2.26"], hosts);
    }
}
