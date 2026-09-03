using Microsoft.Extensions.Logging.Abstractions;
using RxV4A.Core;

namespace RxV4A.Host.Tests;

public sealed class DeviceManagerTests
{
    [Fact]
    public async Task RefreshAndPower_UseOneCapabilityBackedClient()
    {
        using var fixture = new ManagerFixture(includePowerCapability: true);

        await fixture.Manager.RefreshAsync(CancellationToken.None);
        var result = await fixture.Manager.SetMainPowerAsync(MainPower.On, CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Connected, result.Snapshot.ConnectionState);
        Assert.Equal("on", result.Snapshot.MainZone?.Power);
        Assert.Equal([MainPower.On], fixture.Client.PowerCommands);
        Assert.Equal(1, fixture.Client.DeviceInfoCalls);
        Assert.Equal(1, fixture.Client.FeaturesCalls);
        Assert.Equal(1, fixture.Client.AdvancedFeaturesCalls);
    }

    [Fact]
    public async Task Power_IsRejectedWhenCapabilityDoesNotAdvertiseIt()
    {
        using var fixture = new ManagerFixture(includePowerCapability: false);
        await fixture.Manager.RefreshAsync(CancellationToken.None);

        await Assert.ThrowsAsync<CapabilityNotSupportedException>(() =>
            fixture.Manager.SetMainPowerAsync(MainPower.On, CancellationToken.None));

        Assert.Empty(fixture.Client.PowerCommands);
    }

    [Fact]
    public async Task CompatibleModelName_IsAcceptedFromAdvertisedCapabilities()
    {
        using var fixture = new ManagerFixture(includePowerCapability: true);
        fixture.Client.DeviceModels.Enqueue("RX-V6A");

        await fixture.Manager.RefreshAsync(CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Connected, fixture.Manager.Snapshot.ConnectionState);
        Assert.Equal("RX-V6A", fixture.Manager.Snapshot.Capabilities?.DeviceInfo.ModelName);
    }

    [Fact]
    public async Task Volume_IsValidatedAgainstAdvertisedRange()
    {
        using var fixture = new ManagerFixture(includePowerCapability: true);
        await fixture.Manager.RefreshAsync(CancellationToken.None);

        await fixture.Manager.SetMainVolumeAsync(-35.5m, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Manager.SetMainVolumeAsync(-35.25m, CancellationToken.None));

        Assert.Equal([-35.5m], fixture.Client.VolumeCommands);
    }

    [Fact]
    public async Task FailedPoll_MarksDisconnected_AndNextRefreshReconnects()
    {
        using var fixture = new ManagerFixture(includePowerCapability: true);
        await fixture.Manager.RefreshAsync(CancellationToken.None);
        fixture.Client.FailNextStatus = true;

        await fixture.Manager.RefreshAsync(CancellationToken.None);
        Assert.Equal(DeviceConnectionState.Disconnected, fixture.Manager.Snapshot.ConnectionState);

        await fixture.Manager.RefreshAsync(CancellationToken.None);
        Assert.Equal(DeviceConnectionState.Connected, fixture.Manager.Snapshot.ConnectionState);
        Assert.Equal(2, fixture.Client.DeviceInfoCalls);
    }

    [Fact]
    public async Task EmptySsdp_UsesPingFallbackBeforeReadOnlyCapabilityRequests()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { RequestTimeoutSeconds = 2 };
            var client = new FakeYamahaClient(includePowerCapability: true);
            var ping = new FakePingDiscovery("receiver.test");
            using var manager = new DeviceManager(
                settings,
                new JsonSettingsStore(root),
                new EmptyDiscovery(),
                new FakeClientFactory(client),
                new CapabilityStore(),
                NullLogger<DeviceManager>.Instance,
                ping);

            await manager.RefreshAsync();

            Assert.Equal(DeviceConnectionState.Connected, manager.Snapshot.ConnectionState);
            Assert.Equal(1, ping.Calls);
            Assert.False(ping.LastIncludedBroad);
            Assert.Equal(1, client.DeviceInfoCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EmptySsdp_UsesNeighborCacheBeforeSubnetScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { RequestTimeoutSeconds = 2 };
            var client = new FakeYamahaClient(includePowerCapability: true);
            var ping = new FakePingDiscovery("ping-receiver.test");
            using var manager = new DeviceManager(
                settings,
                new JsonSettingsStore(root),
                new EmptyDiscovery(),
                new FakeClientFactory(client),
                new CapabilityStore(),
                NullLogger<DeviceManager>.Instance,
                ping,
                new FakeNeighborDiscovery("neighbor-receiver.test"));

            await manager.RefreshAsync();

            Assert.Equal(DeviceConnectionState.Connected, manager.Snapshot.ConnectionState);
            Assert.Equal(0, ping.Calls);
            Assert.Equal("neighbor-receiver.test", settings.LastDiscoveredHost);
            Assert.Equal(1, client.DeviceInfoCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BroadSubnet_IsNotScannedUntilExplicitlyApproved()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { RequestTimeoutSeconds = 2 };
            var client = new FakeYamahaClient(includePowerCapability: true);
            var ping = new FakePingDiscovery("receiver.test", requiresConfirmation: true);
            using var manager = new DeviceManager(
                settings,
                new JsonSettingsStore(root),
                new EmptyDiscovery(),
                new FakeClientFactory(client),
                new CapabilityStore(),
                NullLogger<DeviceManager>.Instance,
                ping);

            await manager.RefreshAsync();
            Assert.Equal("ping_scan_confirmation_required", manager.Snapshot.ErrorCode);
            Assert.Equal(510, manager.Snapshot.PingScanConfirmation?.HostCount);
            Assert.Equal(0, client.DeviceInfoCalls);

            await manager.ApproveBroadSubnetPingScanAsync();

            Assert.True(ping.LastIncludedBroad);
            Assert.Equal(DeviceConnectionState.Connected, manager.Snapshot.ConnectionState);
            Assert.Equal(1, client.DeviceInfoCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnusableSsdpCandidate_StillFallsBackToPingDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { RequestTimeoutSeconds = 2 };
            var client = new FakeYamahaClient(includePowerCapability: true);
            client.CompatibleFeatureResponses.Enqueue(false);
            client.CompatibleFeatureResponses.Enqueue(true);
            var ping = new FakePingDiscovery("receiver.test");
            using var manager = new DeviceManager(
                settings,
                new JsonSettingsStore(root),
                new StaticDiscovery("other.test"),
                new FakeClientFactory(client),
                new CapabilityStore(),
                NullLogger<DeviceManager>.Instance,
                ping);

            await manager.RefreshAsync();

            Assert.Equal(DeviceConnectionState.Connected, manager.Snapshot.ConnectionState);
            Assert.Equal(1, ping.Calls);
            Assert.Equal(2, client.DeviceInfoCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BroadPingConfirmation_TakesPriorityOverUnrelatedSsdpCandidate_WithoutRetryLoop()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { RequestTimeoutSeconds = 2 };
            var client = new FakeYamahaClient(includePowerCapability: true);
            client.CompatibleFeatureResponses.Enqueue(false);
            var ping = new FakePingDiscovery("receiver.test", requiresConfirmation: true);
            using var manager = new DeviceManager(
                settings,
                new JsonSettingsStore(root),
                new StaticDiscovery("other.test"),
                new FakeClientFactory(client),
                new CapabilityStore(),
                NullLogger<DeviceManager>.Instance,
                ping);

            await manager.RefreshAsync();
            await manager.RefreshAsync();

            Assert.Equal("ping_scan_confirmation_required", manager.Snapshot.ErrorCode);
            Assert.Equal(DeviceConnectionState.Disconnected, manager.Snapshot.ConnectionState);
            Assert.Equal(1, ping.Calls);
            Assert.Equal(1, client.DeviceInfoCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BroadPingConfirmation_RetriesSavedHostWithoutRunningBroadScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings
            {
                LastDiscoveredHost = "receiver.test",
                PreferredDeviceId = "test-device-id",
                RequestTimeoutSeconds = 2
            };
            var client = new FakeYamahaClient(includePowerCapability: true)
            {
                FailNextDeviceInfo = true
            };
            var ping = new FakePingDiscovery("receiver.test", requiresConfirmation: true);
            using var manager = new DeviceManager(
                settings,
                new JsonSettingsStore(root),
                new EmptyDiscovery(),
                new FakeClientFactory(client),
                new CapabilityStore(),
                NullLogger<DeviceManager>.Instance,
                ping);

            await manager.RefreshAsync();
            Assert.Equal("ping_scan_confirmation_required", manager.Snapshot.ErrorCode);
            Assert.Equal(1, ping.Calls);

            await manager.RefreshAsync();

            Assert.Equal(DeviceConnectionState.Connected, manager.Snapshot.ConnectionState);
            Assert.Equal(2, client.DeviceInfoCalls);
            Assert.Equal(1, ping.Calls);
            Assert.False(ping.LastIncludedBroad);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class ManagerFixture : IDisposable
    {
        private readonly string _temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "rxv4a-tests",
            Guid.NewGuid().ToString("N"));

        public ManagerFixture(bool includePowerCapability)
        {
            Client = new FakeYamahaClient(includePowerCapability);
            var settings = new AppSettings
            {
                ManualHost = "receiver.test",
                RequestTimeoutSeconds = 2
            };
            Manager = new DeviceManager(
                settings,
                new JsonSettingsStore(_temporaryRoot),
                new EmptyDiscovery(),
                new FakeClientFactory(Client),
                new CapabilityStore(),
                NullLogger<DeviceManager>.Instance);
        }

        public FakeYamahaClient Client { get; }

        public DeviceManager Manager { get; }

        public void Dispose()
        {
            Manager.Dispose();
            if (Directory.Exists(_temporaryRoot))
            {
                Directory.Delete(_temporaryRoot, true);
            }
        }
    }

    private sealed class FakeClientFactory(FakeYamahaClient client) : IYamahaClientFactory
    {
        public IYamahaClient Create(string host) => client;
    }

    private sealed class EmptyDiscovery : IDeviceDiscovery
    {
        public Task<IReadOnlyList<string>> DiscoverHostsAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StaticDiscovery(params string[] hosts) : IDeviceDiscovery
    {
        public Task<IReadOnlyList<string>> DiscoverHostsAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(hosts);
    }

    private sealed class FakeNeighborDiscovery(params string[] hosts) : INeighborDeviceDiscovery
    {
        public IReadOnlyList<string> GetCandidateHosts(string? networkInterfaceId) => hosts;
    }

    private sealed class FakePingDiscovery(string host, bool requiresConfirmation = false) : IPingSubnetDiscovery
    {
        public int Calls { get; private set; }
        public bool LastIncludedBroad { get; private set; }

        public IReadOnlyList<DiscoveryNetworkInterface> GetNetworkInterfaces() =>
            [new("test-interface", "Test Ethernet", 24, true, 254)];

        public Task<PingSubnetDiscoveryResult> DiscoverHostsAsync(
            bool includeBroadSubnets,
            string? networkInterfaceId,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastIncludedBroad = includeBroadSubnets;
            return Task.FromResult(new PingSubnetDiscoveryResult(
                includeBroadSubnets || !requiresConfirmation ? [host] : [],
                requiresConfirmation && !includeBroadSubnets,
                requiresConfirmation ? 510 : 0,
                requiresConfirmation ? 23 : null,
                false));
        }
    }

    public sealed class FakeYamahaClient(bool includePowerCapability) : IYamahaClient
    {
        public int DeviceInfoCalls { get; private set; }

        public int FeaturesCalls { get; private set; }

        public int AdvancedFeaturesCalls { get; private set; }

        public bool FailNextStatus { get; set; }

        public bool FailNextDeviceInfo { get; set; }

        public List<MainPower> PowerCommands { get; } = [];

        public List<decimal> VolumeCommands { get; } = [];

        public Queue<string> DeviceModels { get; } = [];

        public Queue<bool> CompatibleFeatureResponses { get; } = [];

        public Task<DeviceInfoResponse> GetDeviceInfoAsync(CancellationToken cancellationToken)
        {
            DeviceInfoCalls++;
            if (FailNextDeviceInfo)
            {
                FailNextDeviceInfo = false;
                throw new HttpRequestException("simulated");
            }

            return Task.FromResult(new DeviceInfoResponse
            {
                ModelName = DeviceModels.TryDequeue(out var model) ? model : "RX-V4A",
                DeviceId = "test-device-id",
                ApiVersion = "2.15"
            });
        }

        public Task<FeaturesResponse> GetFeaturesAsync(CancellationToken cancellationToken)
        {
            FeaturesCalls++;
            var compatible = !CompatibleFeatureResponses.TryDequeue(out var queued) || queued;
            return Task.FromResult(new FeaturesResponse
            {
                Zones =
                [
                    new ZoneFeatures
                    {
                        Id = "main",
                        Functions = compatible
                            ? includePowerCapability ? ["power", "volume"] : ["volume"]
                            : ["scene"],
                        Inputs = compatible ? [new InputFeature { Id = "hdmi1" }] : []
                        ,
                        Ranges = compatible
                            ? [new RangeStepFeature { Id = "volume", Minimum = -80.5m, Maximum = 16.5m, Step = 0.5m }]
                            : []
                    }
                ]
            });
        }

        public Task<AdvancedFeaturesResponse> GetAdvancedFeaturesAsync(CancellationToken cancellationToken)
        {
            AdvancedFeaturesCalls++;
            return Task.FromResult(new AdvancedFeaturesResponse());
        }

        public Task<MainZoneStatusResponse> GetMainZoneStatusAsync(CancellationToken cancellationToken)
        {
            if (FailNextStatus)
            {
                FailNextStatus = false;
                throw new HttpRequestException("simulated");
            }

            var power = PowerCommands.LastOrDefault() == MainPower.On ? "on" : "standby";
            return Task.FromResult(new MainZoneStatusResponse
            {
                Power = power,
                Input = "hdmi1",
                Volume = -40m
            });
        }

        public Task SetMainPowerAsync(MainPower power, CancellationToken cancellationToken)
        {
            PowerCommands.Add(power);
            return Task.CompletedTask;
        }

        public Task SetMainInputAsync(string inputId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetMainVolumeAsync(decimal volume, CancellationToken cancellationToken)
        {
            VolumeCommands.Add(volume);
            return Task.CompletedTask;
        }

        public Task SetMainMuteAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMainSoundProgramAsync(string programId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetMainSurround3dAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMainDirectAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMainPureDirectAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMainEnhancerAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMainToneControlAsync(ToneControlSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetMainEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetMainBalanceAsync(decimal value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecallMainSceneAsync(int sceneNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
