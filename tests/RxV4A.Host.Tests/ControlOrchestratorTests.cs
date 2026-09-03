using Microsoft.Extensions.Logging.Abstractions;
using RxV4A.Core;

namespace RxV4A.Host.Tests;

public sealed class ControlOrchestratorTests
{
    private const string ActivityA = "activity-a";
    private const string ActivityB = "activity-b";
    private const string BlockerA = "blocker-a";
    private const string BlockerB = "blocker-b";

    [Fact]
    public async Task OverlappingBlockers_StayBlockedUntilAllAreRemoved_WithoutAutomaticResume()
    {
        var fixture = new Fixture(initialPower: "on");

        await fixture.Orchestrator.ActivateContextAsync(BlockerA);
        await fixture.Orchestrator.ActivateContextAsync(BlockerB);
        await fixture.Orchestrator.DeactivateContextAsync(BlockerA);
        var blocked = await fixture.Orchestrator.ActivateActivityAsync(ActivityA);

        Assert.Equal(ControlOutcome.Blocked, blocked.Outcome);
        Assert.Equal([BlockerB], fixture.Orchestrator.ActiveBlockers);
        Assert.Equal([MainPower.Standby], fixture.Manager.PowerCommands);

        await fixture.Orchestrator.DeactivateContextAsync(BlockerB);

        Assert.Empty(fixture.Orchestrator.ActiveBlockers);
        Assert.Equal([MainPower.Standby], fixture.Manager.PowerCommands);
        Assert.Equal("standby", fixture.Manager.Snapshot.MainZone?.Power);
    }

    [Fact]
    public async Task ExternalPowerOnWhileBlocked_OnlyRaisesWarning_AndRepeatedActivationDoesNotStandby()
    {
        var fixture = new Fixture(initialPower: "standby");
        await fixture.Orchestrator.ActivateContextAsync(BlockerA);

        fixture.Manager.SetExternalPower("on");
        var context = fixture.Orchestrator.GetContexts().Single(item => item.Id == BlockerA);
        var repeated = await fixture.Orchestrator.ActivateContextAsync(BlockerA);

        Assert.True(context.PowerMismatchWarning);
        Assert.Equal(ControlOutcome.AlreadySatisfied, repeated.Outcome);
        Assert.Empty(fixture.Manager.PowerCommands);
        Assert.Equal("on", fixture.Manager.Snapshot.MainZone?.Power);
    }

    [Fact]
    public async Task DirectPowerOn_IsBlockedUnlessCallerExplicitlyAllowsForce()
    {
        var fixture = new Fixture(initialPower: "standby");
        await fixture.Orchestrator.ActivateContextAsync(BlockerA);

        var blocked = await fixture.Orchestrator.SetPowerAsync(MainPower.On, false);
        var forced = await fixture.Orchestrator.SetPowerAsync(MainPower.On, true);

        Assert.Equal(ControlOutcome.Blocked, blocked.Outcome);
        Assert.Equal(ControlOutcome.Succeeded, forced.Outcome);
        Assert.Equal([MainPower.On], fixture.Manager.PowerCommands);
        Assert.True(fixture.Orchestrator.GetContexts().Single(item => item.Active).PowerMismatchWarning);
    }

    [Fact]
    public async Task StandbyFailure_LeavesNewBlockerActiveAndReturnsPartialFailure()
    {
        var fixture = new Fixture(initialPower: "on");
        fixture.Manager.FailNextPowerCommand = true;

        var result = await fixture.Orchestrator.ActivateContextAsync(BlockerA);

        Assert.Equal(ControlOutcome.PartialFailure, result.Outcome);
        Assert.Equal([BlockerA], fixture.Orchestrator.ActiveBlockers);
        Assert.Empty(fixture.Manager.PowerCommands);
    }

    [Fact]
    public async Task RegisteredActionFailure_DoesNotPreventSafetyStandby_AndReturnsPartialFailure()
    {
        var actions = new FakeRegisteredActionService(fail: true);
        var fixture = new Fixture(initialPower: "on", registeredActions: actions);

        var result = await fixture.Orchestrator.ActivateContextAsync(BlockerA);

        Assert.Equal(ControlOutcome.PartialFailure, result.Outcome);
        Assert.Equal("registered_action_failed", result.Code);
        Assert.Equal([MainPower.Standby], fixture.Manager.PowerCommands);
        Assert.Contains(result.Stages,
            stage => stage.Name == "action:example-action" && stage.State == "failed");
        Assert.True(actions.ActivateCalled);
    }

    [Fact]
    public async Task ConfiguredActivity_ConvergesAndRepeatedActivationSkipsRedundantCommands()
    {
        var fixture = new Fixture(initialPower: "standby", configureActivity: true);

        var first = await fixture.Orchestrator.ActivateActivityAsync(ActivityA);
        var second = await fixture.Orchestrator.ActivateActivityAsync(ActivityA);

        Assert.Equal(ControlOutcome.Succeeded, first.Outcome);
        Assert.Equal(ControlOutcome.Succeeded, second.Outcome);
        Assert.Equal([MainPower.On], fixture.Manager.PowerCommands);
        Assert.Equal(["hdmi1"], fixture.Manager.InputCommands);

        await fixture.Orchestrator.DeactivateActivityAsync(ActivityA);
        Assert.Equal([MainPower.On], fixture.Manager.PowerCommands);
    }

    [Fact]
    public async Task UnconfiguredActivity_FailsBeforeSendingAnyDeviceCommand()
    {
        var fixture = new Fixture(initialPower: "standby");

        var result = await fixture.Orchestrator.ActivateActivityAsync(ActivityB);

        Assert.Equal(ControlOutcome.Failed, result.Outcome);
        Assert.Equal("configuration_missing", result.Code);
        Assert.Empty(fixture.Manager.PowerCommands);
        Assert.Empty(fixture.Manager.InputCommands);
    }

    [Fact]
    public async Task ActivityConfiguration_IsCapabilityValidatedAndPersisted()
    {
        var fixture = new Fixture(initialPower: "standby");

        var saved = await fixture.Orchestrator.UpdateActivityConfigurationAsync(
            ActivityB,
            new ActivityConfiguration { InputId = "spotify", StartupDelaySeconds = 2 });

        Assert.True(saved.Configured);
        Assert.Equal("spotify", saved.InputId);
        Assert.Equal(1, fixture.SettingsStore.SaveCount);

        await Assert.ThrowsAsync<CapabilityNotSupportedException>(() =>
            fixture.Orchestrator.UpdateActivityConfigurationAsync(
                ActivityB,
                new ActivityConfiguration { InputId = "not-advertised" }));
        await Assert.ThrowsAsync<CapabilityNotSupportedException>(() =>
            fixture.Orchestrator.UpdateActivityConfigurationAsync(
                ActivityB,
                new ActivityConfiguration { InputId = "alexa" }));
        Assert.Equal(1, fixture.SettingsStore.SaveCount);
    }

    private sealed class Fixture
    {
        public Fixture(
            string initialPower,
            bool configureActivity = false,
            IRegisteredActionService? registeredActions = null)
        {
            var settings = new AppSettings
            {
                PowerOnBlockers = [BlockerA, BlockerB],
                Activities = new Dictionary<string, ActivityConfiguration>(StringComparer.OrdinalIgnoreCase)
                {
                    [ActivityA] = new(),
                    [ActivityB] = new()
                }
            };
            if (configureActivity)
            {
                settings.Activities[ActivityA] = new ActivityConfiguration
                {
                    InputId = "hdmi1",
                    StartupDelaySeconds = 0
                };
            }

            Manager = new FakeDeviceManager(settings, initialPower);
            SettingsStore = new TestSettingsStore(settings);
            Orchestrator = new ControlOrchestrator(
                Manager,
                new PowerOnBlockerRegistry(settings.PowerOnBlockers),
                settings,
                SettingsStore,
                NullLogger<ControlOrchestrator>.Instance,
                registeredActions);
        }

        public FakeDeviceManager Manager { get; }

        public ControlOrchestrator Orchestrator { get; }

        public TestSettingsStore SettingsStore { get; }
    }

    private sealed class FakeRegisteredActionService(bool fail) : IRegisteredActionService
    {
        public bool ActivateCalled { get; private set; }

        public IReadOnlyList<RegisteredActionDescriptor> GetRegisteredActions() => [];

        public Task<RegisteredActionResult> ExecuteAsync(
            string actionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result());

        public Task<ActionSequenceResult> ExecuteSequenceAsync(
            string controlId,
            bool activating,
            CancellationToken cancellationToken = default)
        {
            ActivateCalled = activating;
            return Task.FromResult(new ActionSequenceResult([Result()]));
        }

        private RegisteredActionResult Result() => fail
            ? new RegisteredActionResult("example-action", false, "registered_action_failed", "failed")
            : new RegisteredActionResult("example-action", true, "success", "ok");
    }

    private sealed class FakeDeviceManager : IDeviceManager
    {
        private DeviceSnapshot _snapshot;

        public FakeDeviceManager(AppSettings settings, string initialPower)
        {
            Settings = settings;
            var capabilities = new CapabilitySnapshot(
                new DeviceInfoResponse { ModelName = "RX-V4A", ApiVersion = "2.15" },
                new FeaturesResponse
                {
                    Zones =
                    [
                        new ZoneFeatures
                        {
                            Id = "main",
                            Functions = ["power", "scene"],
                            Inputs =
                            [
                                new InputFeature { Id = "hdmi1" },
                                new InputFeature { Id = "spotify" },
                                new InputFeature { Id = "alexa" }
                            ],
                            SceneCount = 4
                        }
                    ]
                },
                new AdvancedFeaturesResponse());
            _snapshot = new DeviceSnapshot(
                DeviceConnectionState.Connected,
                capabilities,
                new MainZoneStatusResponse { Power = initialPower, Input = "audio1" },
                DateTimeOffset.UtcNow);
        }

        public bool FailNextPowerCommand { get; set; }

        public List<MainPower> PowerCommands { get; } = [];

        public List<string> InputCommands { get; } = [];

        public List<int> SceneCommands { get; } = [];

        public DeviceSnapshot Snapshot => _snapshot;

        public AppSettings Settings { get; }

        public event EventHandler<DeviceSnapshot>? SnapshotChanged;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PowerOperationResult> SetMainPowerAsync(
            MainPower power,
            CancellationToken cancellationToken = default)
        {
            if (FailNextPowerCommand)
            {
                FailNextPowerCommand = false;
                throw new HttpRequestException("simulated");
            }

            PowerCommands.Add(power);
            var value = power == MainPower.On ? "on" : "standby";
            UpdateStatus(_snapshot.MainZone! with { Power = value });
            return Task.FromResult(new PowerOperationResult(power, _snapshot));
        }

        public Task<DeviceSnapshot> SetMainInputAsync(
            string inputId,
            CancellationToken cancellationToken = default)
        {
            InputCommands.Add(inputId);
            UpdateStatus(_snapshot.MainZone! with { Input = inputId });
            return Task.FromResult(_snapshot);
        }

        public Task<DeviceSnapshot> SetMainVolumeAsync(decimal volume, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainMuteAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainSoundProgramAsync(
            string programId,
            CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainSurround3dAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainDirectAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainPureDirectAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainEnhancerAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainToneControlAsync(
            ToneControlSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainEqualizerAsync(
            EqualizerSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> SetMainBalanceAsync(decimal value, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<DeviceSnapshot> RecallMainSceneAsync(
            int sceneNumber,
            CancellationToken cancellationToken = default)
        {
            SceneCommands.Add(sceneNumber);
            return Task.FromResult(_snapshot);
        }

        public Task UpdateManualHostAsync(string? host, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IReadOnlyList<DiscoveryNetworkInterface> GetDiscoveryNetworkInterfaces() => [];

        public Task UpdateConnectionSettingsAsync(
            string? host,
            string? networkInterfaceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApproveBroadSubnetPingScanAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void SetExternalPower(string power) =>
            UpdateStatus(_snapshot.MainZone! with { Power = power });

        private void UpdateStatus(MainZoneStatusResponse status)
        {
            _snapshot = _snapshot with { MainZone = status, UpdatedAt = DateTimeOffset.UtcNow };
            SnapshotChanged?.Invoke(this, _snapshot);
        }
    }
}
