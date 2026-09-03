using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RxV4A.Core;

namespace RxV4A.Host;

public sealed class DeviceManager(
    AppSettings settings,
    ISettingsStore settingsStore,
    IDeviceDiscovery discovery,
    IYamahaClientFactory clientFactory,
    ICapabilityStore capabilityStore,
    ILogger<DeviceManager> logger,
    IPingSubnetDiscovery? pingDiscovery = null,
    INeighborDeviceDiscovery? neighborDiscovery = null) : BackgroundService, IDeviceManager
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private IYamahaClient? _client;
    private DeviceSnapshot _snapshot = DeviceSnapshot.Initial;
    private int? _lastDiscoveryCandidateCount;
    private int? _lastPingCandidateCount;
    private bool _broadPingScanApproved;
    private DateTimeOffset _lastPingScanAt;
    private bool _lastPingScanIncludedBroad;
    private string? _lastPingScanInterfaceId;
    private PingSubnetDiscoveryResult? _cachedPingResult;

    public DeviceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public AppSettings Settings => settings;

    public event EventHandler<DeviceSnapshot>? SnapshotChanged;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                if (Snapshot.ErrorCode == "ping_scan_confirmation_required" && !_broadPingScanApproved)
                {
                    await RetryKnownHostWhileAwaitingPingApprovalAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await RefreshStatusCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RetryKnownHostWhileAwaitingPingApprovalAsync(CancellationToken cancellationToken)
    {
        var usesManualHost = !string.IsNullOrWhiteSpace(settings.ManualHost);
        var knownHost = usesManualHost ? settings.ManualHost : settings.LastDiscoveredHost;
        if (string.IsNullOrWhiteSpace(knownHost))
        {
            return;
        }

        var result = await TryConnectCandidatesAsync([knownHost], cancellationToken).ConfigureAwait(false);
        LogCandidateValidation(usesManualHost ? "manual-retry" : "saved-retry", 1, result);
    }

    public async Task<PowerOperationResult> SetMainPowerAsync(
        MainPower power,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new DeviceUnavailableException("対応アンプに接続されていません。");
            var capabilities = capabilityStore.Current
                ?? throw new DeviceUnavailableException("Capabilityを取得できていません。");
            if (!capabilities.SupportsZoneFunction("main", "power"))
            {
                throw new CapabilityNotSupportedException("main.power");
            }

            using var timeout = CreateTimeout(cancellationToken);
            await client.SetMainPowerAsync(power, timeout.Token).ConfigureAwait(false);

            MainZoneStatusResponse status;
            try
            {
                status = await client.GetMainZoneStatusAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is YamahaException or HttpRequestException or TaskCanceledException)
            {
                status = (Snapshot.MainZone ?? new MainZoneStatusResponse()) with
                {
                    Power = power == MainPower.On ? "on" : "standby"
                };
            }

            Publish(new DeviceSnapshot(
                DeviceConnectionState.Connected,
                capabilities,
                status,
                DateTimeOffset.UtcNow));
            logger.LogInformation("Main Zone power command completed with target {Power}.", power);
            return new PowerOperationResult(power, Snapshot);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DeviceSnapshot> SetMainInputAsync(
        string inputId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputId))
        {
            throw new ArgumentException("An input id is required.", nameof(inputId));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new DeviceUnavailableException("対応アンプに接続されていません。");
            var capabilities = capabilityStore.Current
                ?? throw new DeviceUnavailableException("Capabilityを取得できていません。");
            var zone = capabilities.FindZone("main")
                ?? throw new CapabilityNotSupportedException("main");
            var normalizedInput = inputId.Trim();
            if (!ApplicationScope.IsOperationalInput(normalizedInput) ||
                !zone.Inputs.Any(input =>
                    string.Equals(input.Id, normalizedInput, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CapabilityNotSupportedException($"main.input.{normalizedInput}");
            }

            using var timeout = CreateTimeout(cancellationToken);
            await client.SetMainInputAsync(normalizedInput, timeout.Token).ConfigureAwait(false);
            var status = await client.GetMainZoneStatusAsync(timeout.Token).ConfigureAwait(false);
            Publish(new DeviceSnapshot(
                DeviceConnectionState.Connected,
                capabilities,
                status,
                DateTimeOffset.UtcNow));
            logger.LogInformation("Main Zone input command completed.");
            return Snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<DeviceSnapshot> SetMainVolumeAsync(
        decimal volume,
        CancellationToken cancellationToken = default) =>
        ExecuteMainZoneCommandAsync(
            "volume",
            (capabilities, _) => ValidateRangedValue(capabilities, "volume", volume),
            (client, token) => client.SetMainVolumeAsync(volume, token),
            cancellationToken);

    public Task<DeviceSnapshot> SetMainMuteAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteMainZoneCommandAsync(
            "mute",
            null,
            (client, token) => client.SetMainMuteAsync(enabled, token),
            cancellationToken);

    public Task<DeviceSnapshot> SetMainSoundProgramAsync(
        string programId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(programId))
        {
            throw new ArgumentException("A sound program id is required.", nameof(programId));
        }

        var normalized = programId.Trim();
        return ExecuteMainZoneCommandAsync(
            "sound_program",
            (_, zone) => ValidateListedValue(zone.SoundPrograms, normalized, $"main.sound_program.{normalized}"),
            (client, token) => client.SetMainSoundProgramAsync(normalized, token),
            cancellationToken);
    }

    public Task<DeviceSnapshot> SetMainSurround3dAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteMainZoneCommandAsync(
            "surround_3d",
            null,
            (client, token) => client.SetMainSurround3dAsync(enabled, token),
            cancellationToken);

    public Task<DeviceSnapshot> SetMainDirectAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteMainZoneCommandAsync(
            "direct",
            null,
            (client, token) => client.SetMainDirectAsync(enabled, token),
            cancellationToken);

    public Task<DeviceSnapshot> SetMainPureDirectAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteMainZoneCommandAsync(
            "pure_direct",
            null,
            (client, token) => client.SetMainPureDirectAsync(enabled, token),
            cancellationToken);

    public Task<DeviceSnapshot> SetMainEnhancerAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteMainZoneCommandAsync(
            "enhancer",
            null,
            (client, token) => client.SetMainEnhancerAsync(enabled, token),
            cancellationToken);

    public Task<DeviceSnapshot> SetMainToneControlAsync(
        ToneControlSettings values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return ExecuteMainZoneCommandAsync(
            "tone_control",
            (capabilities, zone) =>
            {
                ValidateMode(zone.ToneControlModes, values.Mode, "main.tone_control.mode");
                ValidateOptionalRangedValue(capabilities, "tone_control", values.Bass);
                ValidateOptionalRangedValue(capabilities, "tone_control", values.Treble);
                RequireAtLeastOneValue(values.Mode, values.Bass, values.Treble);
            },
            (client, token) => client.SetMainToneControlAsync(values, token),
            cancellationToken);
    }

    public Task<DeviceSnapshot> SetMainEqualizerAsync(
        EqualizerSettings values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return ExecuteMainZoneCommandAsync(
            "equalizer",
            (capabilities, zone) =>
            {
                ValidateMode(zone.EqualizerModes, values.Mode, "main.equalizer.mode");
                ValidateOptionalRangedValue(capabilities, "equalizer", values.Low);
                ValidateOptionalRangedValue(capabilities, "equalizer", values.Mid);
                ValidateOptionalRangedValue(capabilities, "equalizer", values.High);
                RequireAtLeastOneValue(values.Mode, values.Low, values.Mid, values.High);
            },
            (client, token) => client.SetMainEqualizerAsync(values, token),
            cancellationToken);
    }

    public Task<DeviceSnapshot> SetMainBalanceAsync(
        decimal value,
        CancellationToken cancellationToken = default) =>
        ExecuteMainZoneCommandAsync(
            "balance",
            (capabilities, _) => ValidateRangedValue(capabilities, "balance", value),
            (client, token) => client.SetMainBalanceAsync(value, token),
            cancellationToken);

    public async Task<DeviceSnapshot> RecallMainSceneAsync(
        int sceneNumber,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new DeviceUnavailableException("対応アンプに接続されていません。");
            var capabilities = capabilityStore.Current
                ?? throw new DeviceUnavailableException("Capabilityを取得できていません。");
            var zone = capabilities.FindZone("main")
                ?? throw new CapabilityNotSupportedException("main");
            if (!capabilities.SupportsZoneFunction("main", "scene") ||
                zone.SceneCount is not int sceneCount ||
                sceneNumber < 1 ||
                sceneNumber > sceneCount)
            {
                throw new CapabilityNotSupportedException($"main.scene.{sceneNumber}");
            }

            using var timeout = CreateTimeout(cancellationToken);
            await client.RecallMainSceneAsync(sceneNumber, timeout.Token).ConfigureAwait(false);
            var status = await client.GetMainZoneStatusAsync(timeout.Token).ConfigureAwait(false);
            Publish(new DeviceSnapshot(
                DeviceConnectionState.Connected,
                capabilities,
                status,
                DateTimeOffset.UtcNow));
            logger.LogInformation("Main Zone scene recall completed.");
            return Snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public IReadOnlyList<DiscoveryNetworkInterface> GetDiscoveryNetworkInterfaces() =>
        pingDiscovery?.GetNetworkInterfaces() ?? [];

    public Task UpdateManualHostAsync(string? host, CancellationToken cancellationToken = default) =>
        UpdateConnectionSettingsAsync(host, settings.DiscoveryNetworkInterfaceId, cancellationToken);

    public async Task UpdateConnectionSettingsAsync(
        string? host,
        string? networkInterfaceId,
        CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        if (normalized is not null)
        {
            _ = YamahaClient.CreateBaseUri(normalized);
        }

        var availableInterfaces = GetDiscoveryNetworkInterfaces();
        var normalizedInterfaceId = string.IsNullOrWhiteSpace(networkInterfaceId)
            ? null
            : networkInterfaceId.Trim();
        if (normalizedInterfaceId is not null && !availableInterfaces.Any(item =>
                string.Equals(item.Id, normalizedInterfaceId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The selected network interface is not available.", nameof(networkInterfaceId));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (settings.SyncRoot)
            {
                settings.ManualHost = normalized;
                settings.DiscoveryNetworkInterfaceId = normalizedInterfaceId;
            }
            await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            _client = null;
            _broadPingScanApproved = false;
            _cachedPingResult = null;
            _lastDiscoveryCandidateCount = null;
            _lastPingCandidateCount = null;
            capabilityStore.Clear();
            Publish(DeviceSnapshot.Initial with { UpdatedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            _operationGate.Release();
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApproveBroadSubnetPingScanAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _broadPingScanApproved = true;
            _cachedPingResult = null;
            Publish(Snapshot with
            {
                ConnectionState = DeviceConnectionState.Connecting,
                ErrorCode = null,
                PingScanConfirmation = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            _operationGate.Release();
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Device synchronization failed. FailureType={FailureType}",
                    exception.GetType().Name);
                MarkDisconnected("device_unavailable");
            }

            var delaySeconds = Snapshot.ConnectionState == DeviceConnectionState.Connected
                ? Math.Clamp(settings.PollIntervalSeconds, 2, 3600)
                : Math.Clamp(settings.ReconnectIntervalSeconds, 2, 3600);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.ErrorCode is null || Snapshot.Capabilities is not null)
        {
            Publish(Snapshot with
            {
                ConnectionState = Snapshot.Capabilities is null
                    ? DeviceConnectionState.Connecting
                    : DeviceConnectionState.Reconnecting,
                ErrorCode = null,
                PingScanConfirmation = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        IReadOnlyList<string> candidates;
        PingSubnetDiscoveryResult? pingResult = null;
        var usesManualHost = !string.IsNullOrWhiteSpace(settings.ManualHost);
        var selectedInterfaceId = GetEffectiveNetworkInterfaceId();
        if (usesManualHost)
        {
            candidates = [settings.ManualHost!];
        }
        else
        {
            var savedCandidates = string.IsNullOrWhiteSpace(settings.LastDiscoveredHost)
                ? Array.Empty<string>()
                : new[] { settings.LastDiscoveredHost! };
            var savedResult = await TryConnectCandidatesAsync(savedCandidates, cancellationToken).ConfigureAwait(false);
            LogCandidateValidation("saved", savedCandidates.Length, savedResult);
            if (savedResult.Connected)
            {
                return;
            }

            candidates = await discovery.DiscoverHostsAsync(
                    TimeSpan.FromSeconds(3),
                    selectedInterfaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_lastDiscoveryCandidateCount != candidates.Count)
            {
                _lastDiscoveryCandidateCount = candidates.Count;
                logger.LogInformation(
                    "SSDP discovery completed. CandidateCount={CandidateCount}",
                    candidates.Count);
            }


        }

        var initialResult = await TryConnectCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
        LogCandidateValidation(usesManualHost ? "manual" : "ssdp", candidates.Count, initialResult);
        if (initialResult.Connected)
        {
            return;
        }

        var sawUnsupportedDevice = initialResult.SawUnsupportedDevice;
        if (!usesManualHost && neighborDiscovery is not null)
        {
            var neighborCandidates = neighborDiscovery.GetCandidateHosts(selectedInterfaceId)
                .Except(candidates, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            logger.LogInformation(
                "Windows neighbor-cache discovery completed. CandidateCount={CandidateCount}",
                neighborCandidates.Length);
            var neighborConnection = await TryConnectCandidatesAsync(
                    neighborCandidates,
                    cancellationToken,
                    probeConcurrently: true)
                .ConfigureAwait(false);
            LogCandidateValidation("neighbor", neighborCandidates.Length, neighborConnection);
            sawUnsupportedDevice |= neighborConnection.SawUnsupportedDevice;
            if (neighborConnection.Connected)
            {
                return;
            }
        }

        if (!usesManualHost && pingDiscovery is not null)
        {
            pingResult = await GetPingCandidatesAsync(cancellationToken).ConfigureAwait(false);
            var pingCandidates = pingResult.Hosts
                .Except(candidates, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (_lastPingCandidateCount != pingCandidates.Length)
            {
                _lastPingCandidateCount = pingCandidates.Length;
                logger.LogInformation(
                    "Ping fallback discovery completed. ScannedHostCount={ScannedHostCount} PingResponsiveCount={PingResponsiveCount} HttpCandidateCount={HttpCandidateCount}",
                    pingResult.ScannedHostCount,
                    pingResult.PingResponsiveCount,
                    pingCandidates.Length);
            }

            var pingConnection = await TryConnectCandidatesAsync(pingCandidates, cancellationToken)
                .ConfigureAwait(false);
            LogCandidateValidation("ping", pingCandidates.Length, pingConnection);
            sawUnsupportedDevice |= pingConnection.SawUnsupportedDevice;
            if (pingConnection.Connected)
            {
                return;
            }
        }

        _client = null;
        capabilityStore.Clear();
        var requiresConfirmation = pingResult?.ConfirmationRequired == true;
        var tooLarge = pingResult?.TooLargeSubnetPresent == true;
        var errorCode = requiresConfirmation
            ? "ping_scan_confirmation_required"
            : sawUnsupportedDevice
                ? "unsupported_device"
                : tooLarge
                    ? "ping_scan_too_large"
                    : "device_not_found";
        var confirmation = requiresConfirmation && pingResult?.BroadestPrefixLength is int prefix
            ? new PingScanConfirmation(pingResult.ConfirmationHostCount, prefix)
            : null;
        Publish(new DeviceSnapshot(
            sawUnsupportedDevice && !requiresConfirmation
                ? DeviceConnectionState.Unsupported
                : DeviceConnectionState.Disconnected,
            null,
            null,
            DateTimeOffset.UtcNow,
            errorCode,
            confirmation));
    }

    private async Task<CandidateConnectionResult> TryConnectCandidatesAsync(
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken,
        bool probeConcurrently = false)
    {
        var sawUnsupportedDevice = false;
        var deviceInfoFailures = 0;
        var modelMismatches = 0;
        var preferredDeviceMismatches = 0;
        var featureFailures = 0;
        var advancedFeatureFailures = 0;
        var statusFailures = 0;
        var concurrentProbes = probeConcurrently
            ? await ProbeDeviceInfoConcurrentlyAsync(candidates, cancellationToken).ConfigureAwait(false)
            : null;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var host = candidates[candidateIndex];
            IYamahaClient client;
            DeviceInfoResponse deviceInfo;
            if (concurrentProbes is not null)
            {
                var probe = concurrentProbes[candidateIndex];
                if (probe is null)
                {
                    deviceInfoFailures++;
                    continue;
                }

                client = probe.Client;
                deviceInfo = probe.DeviceInfo;
            }
            else
            {
                try
                {
                    client = clientFactory.Create(host);
                    using var timeout = CreateTimeout(cancellationToken);
                    deviceInfo = await client.GetDeviceInfoAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is YamahaException or HttpRequestException or TaskCanceledException or ArgumentException)
                {
                    deviceInfoFailures++;
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(settings.ManualHost) &&
                !string.IsNullOrWhiteSpace(settings.PreferredDeviceId) &&
                !string.Equals(settings.PreferredDeviceId, deviceInfo.DeviceId, StringComparison.Ordinal))
            {
                preferredDeviceMismatches++;
                continue;
            }

            FeaturesResponse features;
            try
            {
                using var timeout = CreateTimeout(cancellationToken);
                features = await client.GetFeaturesAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is YamahaException or HttpRequestException or TaskCanceledException)
            {
                featureFailures++;
                continue;
            }

            if (!IsCompatibleDevice(deviceInfo, features))
            {
                sawUnsupportedDevice = true;
                modelMismatches++;
                continue;
            }

            var advancedFeatures = new AdvancedFeaturesResponse { ResponseCode = 0 };
            try
            {
                using var timeout = CreateTimeout(cancellationToken);
                advancedFeatures = await client.GetAdvancedFeaturesAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is YamahaException or HttpRequestException or TaskCanceledException)
            {
                advancedFeatureFailures++;
            }

            MainZoneStatusResponse mainStatus;
            try
            {
                using var timeout = CreateTimeout(cancellationToken);
                mainStatus = await client.GetMainZoneStatusAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is YamahaException or HttpRequestException or TaskCanceledException)
            {
                statusFailures++;
                continue;
            }

            var capabilities = new CapabilitySnapshot(deviceInfo, features, advancedFeatures);
            _client = client;
            capabilityStore.Replace(capabilities);
            bool shouldUpdatePreferredDevice;
            lock (settings.SyncRoot)
            {
                shouldUpdatePreferredDevice = !string.IsNullOrWhiteSpace(deviceInfo.DeviceId) &&
                    (string.IsNullOrWhiteSpace(settings.PreferredDeviceId) ||
                     !string.IsNullOrWhiteSpace(settings.ManualHost));
            }
            if (shouldUpdatePreferredDevice)
            {
                lock (settings.SyncRoot)
                {
                    settings.PreferredDeviceId = deviceInfo.DeviceId;
                }
            }

            var shouldUpdateDiscoveredHost = string.IsNullOrWhiteSpace(settings.ManualHost) &&
                !string.Equals(settings.LastDiscoveredHost, host, StringComparison.OrdinalIgnoreCase);
            if (shouldUpdateDiscoveredHost)
            {
                lock (settings.SyncRoot)
                {
                    settings.LastDiscoveredHost = host;
                }
            }

            if (shouldUpdatePreferredDevice || shouldUpdateDiscoveredHost)
            {
                await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            Publish(new DeviceSnapshot(
                DeviceConnectionState.Connected,
                capabilities,
                mainStatus,
                DateTimeOffset.UtcNow));
            logger.LogInformation("Compatible Yamaha device connected and capabilities refreshed.");
            return new CandidateConnectionResult(
                true,
                sawUnsupportedDevice,
                deviceInfoFailures,
                modelMismatches,
                preferredDeviceMismatches,
                featureFailures,
                advancedFeatureFailures,
                statusFailures);
        }

        return new CandidateConnectionResult(
            false,
            sawUnsupportedDevice,
            deviceInfoFailures,
            modelMismatches,
            preferredDeviceMismatches,
            featureFailures,
            advancedFeatureFailures,
            statusFailures);
    }

    private async Task<CandidateProbe?[]> ProbeDeviceInfoConcurrentlyAsync(
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        var results = new CandidateProbe?[candidates.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 32,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                try
                {
                    var client = clientFactory.Create(candidates[index]);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));
                    var deviceInfo = await client.GetDeviceInfoAsync(timeout.Token).ConfigureAwait(false);
                    results[index] = new CandidateProbe(client, deviceInfo);
                }
                catch (Exception exception) when (
                    exception is YamahaException or HttpRequestException or TaskCanceledException or ArgumentException)
                {
                    // Non-Yamaha and unreachable neighbor entries are expected.
                }
            }).ConfigureAwait(false);
        return results;
    }

    private void LogCandidateValidation(string source, int candidateCount, CandidateConnectionResult result) =>
        logger.LogInformation(
            "Candidate validation completed. Source={Source} CandidateCount={CandidateCount} Connected={Connected} DeviceInfoFailures={DeviceInfoFailures} CompatibilityMismatches={CompatibilityMismatches} PreferredDeviceMismatches={PreferredDeviceMismatches} FeatureFailures={FeatureFailures} AdvancedFeatureFailures={AdvancedFeatureFailures} StatusFailures={StatusFailures}",
            source,
            candidateCount,
            result.Connected,
            result.DeviceInfoFailures,
            result.ModelMismatches,
            result.PreferredDeviceMismatches,
            result.FeatureFailures,
            result.AdvancedFeatureFailures,
            result.StatusFailures);

    private sealed record CandidateConnectionResult(
        bool Connected,
        bool SawUnsupportedDevice,
        int DeviceInfoFailures,
        int ModelMismatches,
        int PreferredDeviceMismatches,
        int FeatureFailures,
        int AdvancedFeatureFailures,
        int StatusFailures);

    private sealed record CandidateProbe(IYamahaClient Client, DeviceInfoResponse DeviceInfo);

    private async Task<DeviceSnapshot> ExecuteMainZoneCommandAsync(
        string capability,
        Action<CapabilitySnapshot, ZoneFeatures>? validate,
        Func<IYamahaClient, CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new DeviceUnavailableException("対応アンプに接続されていません。");
            var capabilities = capabilityStore.Current
                ?? throw new DeviceUnavailableException("Capabilityを取得できていません。");
            var zone = capabilities.FindZone("main")
                ?? throw new CapabilityNotSupportedException("main");
            if (!capabilities.SupportsZoneFunction("main", capability))
            {
                throw new CapabilityNotSupportedException($"main.{capability}");
            }

            validate?.Invoke(capabilities, zone);
            using var timeout = CreateTimeout(cancellationToken);
            await command(client, timeout.Token).ConfigureAwait(false);
            var status = await client.GetMainZoneStatusAsync(timeout.Token).ConfigureAwait(false);
            Publish(new DeviceSnapshot(
                DeviceConnectionState.Connected,
                capabilities,
                status,
                DateTimeOffset.UtcNow));
            logger.LogInformation("Main Zone command completed. Capability={Capability}", capability);
            return Snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static bool IsCompatibleDevice(DeviceInfoResponse deviceInfo, FeaturesResponse features)
    {
        if (string.IsNullOrWhiteSpace(deviceInfo.ModelName))
        {
            return false;
        }

        var main = features.Zones.FirstOrDefault(zone =>
            string.Equals(zone.Id, "main", StringComparison.OrdinalIgnoreCase));
        return main is not null &&
               main.Inputs.Any(input => !string.IsNullOrWhiteSpace(input.Id)) &&
               (main.Functions.Contains("power", StringComparer.OrdinalIgnoreCase) ||
                main.Functions.Contains("volume", StringComparer.OrdinalIgnoreCase) ||
                main.Functions.Contains("mute", StringComparer.OrdinalIgnoreCase));
    }

    private static void ValidateListedValue(
        IReadOnlyList<string> choices,
        string value,
        string capability)
    {
        if (!choices.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            throw new CapabilityNotSupportedException(capability);
        }
    }

    private static void ValidateMode(
        IReadOnlyList<string> advertisedModes,
        string? mode,
        string capability)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        if (advertisedModes.Count == 0)
        {
            if (!string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase))
            {
                throw new CapabilityNotSupportedException(capability);
            }

            return;
        }

        ValidateListedValue(advertisedModes, mode.Trim(), capability);
    }

    private static void ValidateOptionalRangedValue(
        CapabilitySnapshot capabilities,
        string rangeId,
        decimal? value)
    {
        if (value.HasValue)
        {
            ValidateRangedValue(capabilities, rangeId, value.Value);
        }
    }

    private static void ValidateRangedValue(
        CapabilitySnapshot capabilities,
        string rangeId,
        decimal value)
    {
        var range = capabilities.FindZoneRange("main", rangeId)
            ?? throw new CapabilityNotSupportedException($"main.{rangeId}.range");
        if (range.Minimum is not decimal minimum ||
            range.Maximum is not decimal maximum ||
            range.Step is not decimal step ||
            step <= 0)
        {
            throw new CapabilityNotSupportedException($"main.{rangeId}.range");
        }

        if (value < minimum || value > maximum || (value - minimum) % step != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Value must be between {minimum} and {maximum} in increments of {step}.");
        }
    }

    private static void RequireAtLeastOneValue(params object?[] values)
    {
        if (values.All(value => value is null || value is string text && string.IsNullOrWhiteSpace(text)))
        {
            throw new ArgumentException("At least one setting value is required.", nameof(values));
        }
    }

    private async Task<PingSubnetDiscoveryResult> GetPingCandidatesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var interfaceId = GetEffectiveNetworkInterfaceId();
        if (_cachedPingResult is not null &&
            _lastPingScanIncludedBroad == _broadPingScanApproved &&
            string.Equals(_lastPingScanInterfaceId, interfaceId, StringComparison.OrdinalIgnoreCase) &&
            now - _lastPingScanAt < TimeSpan.FromMinutes(1))
        {
            return _cachedPingResult;
        }

        _cachedPingResult = await pingDiscovery!.DiscoverHostsAsync(
                _broadPingScanApproved,
                interfaceId,
                cancellationToken)
            .ConfigureAwait(false);
        _lastPingScanIncludedBroad = _broadPingScanApproved;
        _lastPingScanInterfaceId = interfaceId;
        _lastPingScanAt = now;
        return _cachedPingResult;
    }

    private string? GetEffectiveNetworkInterfaceId()
    {
        var configuredId = settings.DiscoveryNetworkInterfaceId;
        return string.IsNullOrWhiteSpace(configuredId) || !GetDiscoveryNetworkInterfaces().Any(item =>
            string.Equals(item.Id, configuredId, StringComparison.OrdinalIgnoreCase))
            ? null
            : configuredId;
    }

    private async Task RefreshStatusCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var status = await _client!.GetMainZoneStatusAsync(timeout.Token).ConfigureAwait(false);
            Publish(Snapshot with
            {
                ConnectionState = DeviceConnectionState.Connected,
                MainZone = status,
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = null,
                PingScanConfirmation = null
            });
        }
        catch (Exception exception) when (exception is YamahaException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                "Yamaha device status refresh failed. FailureType={FailureType}",
                exception.GetType().Name);
            MarkDisconnected("device_unavailable");
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 1, 60)));
        return timeout;
    }

    private void MarkDisconnected(string errorCode)
    {
        _client = null;
        Publish(Snapshot with
        {
            ConnectionState = DeviceConnectionState.Disconnected,
            ErrorCode = errorCode,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private void Publish(DeviceSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
