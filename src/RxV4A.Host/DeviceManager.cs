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
            var client = _client ?? throw new DeviceUnavailableException("RX-V4Aに接続されていません。");
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
            var client = _client ?? throw new DeviceUnavailableException("RX-V4Aに接続されていません。");
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

    public async Task<DeviceSnapshot> RecallMainSceneAsync(
        int sceneNumber,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new DeviceUnavailableException("RX-V4Aに接続されていません。");
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

            if (!string.Equals(deviceInfo.ModelName, "RX-V4A", StringComparison.OrdinalIgnoreCase))
            {
                sawUnsupportedDevice = true;
                modelMismatches++;
                continue;
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

            AdvancedFeaturesResponse advancedFeatures;
            try
            {
                using var timeout = CreateTimeout(cancellationToken);
                advancedFeatures = await client.GetAdvancedFeaturesAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is YamahaException or HttpRequestException or TaskCanceledException)
            {
                advancedFeatureFailures++;
                continue;
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
            logger.LogInformation("RX-V4A connected and capabilities refreshed.");
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
            "Candidate validation completed. Source={Source} CandidateCount={CandidateCount} Connected={Connected} DeviceInfoFailures={DeviceInfoFailures} ModelMismatches={ModelMismatches} PreferredDeviceMismatches={PreferredDeviceMismatches} FeatureFailures={FeatureFailures} AdvancedFeatureFailures={AdvancedFeatureFailures} StatusFailures={StatusFailures}",
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
                "RX-V4A status refresh failed. FailureType={FailureType}",
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
