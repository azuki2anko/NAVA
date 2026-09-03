using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RxV4A.Core;

namespace RxV4A.Host.Tests;

public sealed class LocalApiContractTests
{
    [Fact]
    public async Task OpenApiAndTypedPowerEndpoint_AreAvailableOnLoopback()
    {
        var manager = new ApiFakeDeviceManager();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDeviceManager>(manager);
        builder.Services.AddSingleton<IControlOrchestrator>(new ControlOrchestrator(
            manager,
            new PowerOnBlockerRegistry(manager.Settings.PowerOnBlockers),
            manager.Settings,
            new TestSettingsStore(manager.Settings),
            NullLogger<ControlOrchestrator>.Instance));
        builder.Services.AddSingleton<IRegisteredActionService, EmptyRegisteredActionService>();
        builder.Services.AddOpenApi("v1");

        await using var application = builder.Build();
        LocalApiApplication.MapApiEndpoints(application);
        application.MapOpenApi("/openapi/{documentName}.json");
        await application.StartAsync(CancellationToken.None);

        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseAddress = new Uri(addresses.Single());
        using var httpClient = new HttpClient { BaseAddress = baseAddress };

        var openApi = await httpClient.GetStringAsync("/openapi/v1.json", CancellationToken.None);
        Assert.Contains("/api/v1/zones/main/power", openApi, StringComparison.Ordinal);
        Assert.Contains("/api/v1/zones/main/volume", openApi, StringComparison.Ordinal);
        Assert.Contains("/api/v1/zones/main/processing/pure-direct", openApi, StringComparison.Ordinal);
        Assert.Contains("/api/v1/zones/main/equalizer", openApi, StringComparison.Ordinal);
        Assert.Contains("/api/v1/contexts/{contextId}:activate", openApi, StringComparison.Ordinal);
        Assert.Contains("/api/v1/actions/{actionId}:execute", openApi, StringComparison.Ordinal);

        var response = await httpClient.PutAsJsonAsync(
            "/api/v1/zones/main/power",
            new { power = "on" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MainPower.On, manager.LastPower);

        var volumeResponse = await httpClient.PutAsJsonAsync(
            "/api/v1/zones/main/volume",
            new { volume = -35.5m },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, volumeResponse.StatusCode);
        Assert.Equal(-35.5m, manager.LastVolume);

        var contextResponse = await httpClient.PostAsync(
            "/api/v1/contexts/example-blocker:activate",
            null,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        Assert.Equal(MainPower.Standby, manager.LastPower);

        var blockedResponse = await httpClient.PutAsJsonAsync(
            "/api/v1/zones/main/power",
            new { power = "on" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, blockedResponse.StatusCode);

        var forceResponse = await httpClient.PutAsJsonAsync(
            "/api/v1/zones/main/power",
            new { power = "on", force = true },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, forceResponse.StatusCode);

        await application.StopAsync(CancellationToken.None);
    }

    private sealed class EmptyRegisteredActionService : IRegisteredActionService
    {
        public IReadOnlyList<RegisteredActionDescriptor> GetRegisteredActions() => [];

        public Task<RegisteredActionResult> ExecuteAsync(
            string actionId,
            CancellationToken cancellationToken = default) =>
            throw new ArgumentOutOfRangeException(nameof(actionId));

        public Task<ActionSequenceResult> ExecuteSequenceAsync(
            string controlId,
            bool activating,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActionSequenceResult([]));
    }

    private sealed class ApiFakeDeviceManager : IDeviceManager
    {
        private readonly CapabilitySnapshot _capabilities = new(
            new DeviceInfoResponse { ModelName = "RX-V4A", ApiVersion = "2.15" },
            new FeaturesResponse
            {
                Zones = [new ZoneFeatures { Id = "main", Functions = ["power"] }]
            },
            new AdvancedFeaturesResponse());

        public MainPower? LastPower { get; private set; }

        public decimal? LastVolume { get; private set; }

        public DeviceSnapshot Snapshot => new(
            DeviceConnectionState.Connected,
            _capabilities,
            new MainZoneStatusResponse { Power = LastPower == MainPower.On ? "on" : "standby" },
            DateTimeOffset.UtcNow);

        public AppSettings Settings { get; } = new() { PowerOnBlockers = ["example-blocker"] };

        public event EventHandler<DeviceSnapshot>? SnapshotChanged;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PowerOperationResult> SetMainPowerAsync(
            MainPower power,
            CancellationToken cancellationToken = default)
        {
            LastPower = power;
            SnapshotChanged?.Invoke(this, Snapshot);
            return Task.FromResult(new PowerOperationResult(power, Snapshot));
        }

        public Task<DeviceSnapshot> SetMainInputAsync(
            string inputId,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainVolumeAsync(decimal volume, CancellationToken cancellationToken = default)
        {
            LastVolume = volume;
            return Task.FromResult(Snapshot);
        }

        public Task<DeviceSnapshot> SetMainMuteAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainSoundProgramAsync(
            string programId,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainSurround3dAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainDirectAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainPureDirectAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainEnhancerAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainToneControlAsync(
            ToneControlSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainEqualizerAsync(
            EqualizerSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> SetMainBalanceAsync(decimal value, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<DeviceSnapshot> RecallMainSceneAsync(
            int sceneNumber,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public Task UpdateManualHostAsync(string? host, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IReadOnlyList<DiscoveryNetworkInterface> GetDiscoveryNetworkInterfaces() => [];

        public Task UpdateConnectionSettingsAsync(
            string? host,
            string? networkInterfaceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApproveBroadSubnetPingScanAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
