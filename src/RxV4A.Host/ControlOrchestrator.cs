using Microsoft.Extensions.Logging;
using RxV4A.Core;

namespace RxV4A.Host;

public enum ControlOutcome
{
    Succeeded,
    AlreadySatisfied,
    Blocked,
    PartialFailure,
    Failed
}

public sealed record ControlStage(string Name, string State, string? Code = null);

public sealed record ControlOperationResult(
    string RequestId,
    string OperationId,
    ControlOutcome Outcome,
    string Code,
    string Message,
    IReadOnlyList<ControlStage> Stages,
    IReadOnlyList<string> ActiveBlockers,
    DeviceSnapshot Snapshot);

public sealed record ContextStatus(string Id, bool Active, bool PowerMismatchWarning);

public sealed record ActivityStatus(
    string Id,
    bool Active,
    bool Configured,
    string? InputId,
    int? SceneNumber);

public interface IControlOrchestrator
{
    IReadOnlyList<string> ActiveBlockers { get; }

    event EventHandler? StateChanged;

    IReadOnlyList<ContextStatus> GetContexts();

    IReadOnlyList<ActivityStatus> GetActivities();

    Task<ControlOperationResult> SetPowerAsync(
        MainPower power,
        bool allowBlockedOn,
        CancellationToken cancellationToken = default);

    Task<ControlOperationResult> ActivateContextAsync(
        string contextId,
        CancellationToken cancellationToken = default);

    Task<ControlOperationResult> DeactivateContextAsync(
        string contextId,
        CancellationToken cancellationToken = default);

    Task<ControlOperationResult> ActivateActivityAsync(
        string activityId,
        CancellationToken cancellationToken = default);

    Task<ControlOperationResult> DeactivateActivityAsync(
        string activityId,
        CancellationToken cancellationToken = default);

    Task<ActivityStatus> UpdateActivityConfigurationAsync(
        string activityId,
        ActivityConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public sealed class ControlOrchestrator : IControlOrchestrator, IDisposable
{
    private readonly IDeviceManager _deviceManager;
    private readonly IPowerOnBlockerRegistry _blockers;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<ControlOrchestrator> _logger;
    private readonly IRegisteredActionService? _registeredActions;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Lock _activitySync = new();
    private readonly HashSet<string> _activeActivities = new(StringComparer.OrdinalIgnoreCase);

    public ControlOrchestrator(
        IDeviceManager deviceManager,
        IPowerOnBlockerRegistry blockers,
        AppSettings settings,
        ISettingsStore settingsStore,
        ILogger<ControlOrchestrator> logger,
        IRegisteredActionService? registeredActions = null)
    {
        _deviceManager = deviceManager;
        _blockers = blockers;
        _settings = settings;
        _settingsStore = settingsStore;
        _logger = logger;
        _registeredActions = registeredActions;
        _blockers.Changed += State_Changed;
        _deviceManager.SnapshotChanged += DeviceManager_SnapshotChanged;
    }

    public IReadOnlyList<string> ActiveBlockers => _blockers.Active;

    public event EventHandler? StateChanged;

    public IReadOnlyList<ContextStatus> GetContexts()
    {
        var powerIsOn = string.Equals(
            _deviceManager.Snapshot.MainZone?.Power,
            "on",
            StringComparison.OrdinalIgnoreCase);
        return _blockers.Registered
            .Select(id => new ContextStatus(
                id,
                _blockers.IsActive(id),
                _blockers.IsActive(id) && powerIsOn))
            .ToArray();
    }

    public IReadOnlyList<ActivityStatus> GetActivities()
    {
        Dictionary<string, ActivityConfiguration?> configurations;
        lock (_settings.SyncRoot)
        {
            configurations = _settings.Activities.Keys.ToDictionary(
                id => id,
                id => _settings.Activities.TryGetValue(id, out var value) ? value : null,
                StringComparer.OrdinalIgnoreCase);
        }

        lock (_activitySync)
        {
            return configurations.Keys.Select(id =>
            {
                var configuration = configurations[id];
                var configured = configuration is not null &&
                    (configuration.InputId is not null ^ configuration.SceneNumber.HasValue);
                return new ActivityStatus(
                    id,
                    _activeActivities.Contains(id),
                    configured,
                    configuration?.InputId,
                    configuration?.SceneNumber);
            }).ToArray();
        }
    }

    public async Task<ControlOperationResult> SetPowerAsync(
        MainPower power,
        bool allowBlockedOn,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationId = power == MainPower.On ? "power-on" : "power-standby";
            if (power == MainPower.On && _blockers.IsBlocked && !allowBlockedOn)
            {
                return Result(
                    operationId,
                    ControlOutcome.Blocked,
                    "blocked_by_context",
                    "電源ONは禁止コンテキストによりブロックされました。",
                    []);
            }

            var target = power == MainPower.On ? "on" : "standby";
            if (string.Equals(_deviceManager.Snapshot.MainZone?.Power, target, StringComparison.OrdinalIgnoreCase))
            {
                return Result(
                    operationId,
                    ControlOutcome.AlreadySatisfied,
                    "already_satisfied",
                    "すでに目的の電源状態です。",
                    [new ControlStage("power", "skipped")]);
            }

            await _deviceManager.SetMainPowerAsync(power, cancellationToken).ConfigureAwait(false);
            if (power == MainPower.On && allowBlockedOn && _blockers.IsBlocked)
            {
                _logger.LogWarning(
                    "A confirmed force-on was executed while blockers were active. BlockerCount={BlockerCount}",
                    _blockers.Active.Count);
            }

            return Result(
                operationId,
                ControlOutcome.Succeeded,
                "success",
                "電源操作を完了しました。",
                [new ControlStage("power", "completed")]);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return FailureResult("power", exception, false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> ActivateContextAsync(
        string contextId,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(contextId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stages = new List<ControlStage>();
            var activated = _blockers.Activate(contextId);
            stages.Add(new ControlStage("blocker", activated ? "completed" : "skipped"));
            if (!activated)
            {
                return Result(
                    $"context:{contextId}:activate",
                    ControlOutcome.AlreadySatisfied,
                    "already_active",
                    "コンテキストはすでに有効です。アンプ状態は変更しません。",
                    stages);
            }

            var actionsSucceeded = await ExecuteActionSequenceAsync(contextId, true, stages, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(
                    _deviceManager.Snapshot.MainZone?.Power,
                    "standby",
                    StringComparison.OrdinalIgnoreCase))
            {
                stages.Add(new ControlStage("standby", "skipped"));
                return Result(
                    $"context:{contextId}:activate",
                    actionsSucceeded ? ControlOutcome.Succeeded : ControlOutcome.PartialFailure,
                    actionsSucceeded ? "success" : "registered_action_failed",
                    actionsSucceeded
                        ? "コンテキストを有効化しました。"
                        : "コンテキストを有効化しましたが、一部の登録済みアクションを実行できませんでした。",
                    stages);
            }

            try
            {
                await _deviceManager.SetMainPowerAsync(MainPower.Standby, cancellationToken).ConfigureAwait(false);
                stages.Add(new ControlStage("standby", "completed"));
                return Result(
                    $"context:{contextId}:activate",
                    actionsSucceeded ? ControlOutcome.Succeeded : ControlOutcome.PartialFailure,
                    actionsSucceeded ? "success" : "registered_action_failed",
                    actionsSucceeded
                        ? "コンテキストを有効化し、アンプをStandbyにしました。"
                        : "アンプをStandbyにしましたが、一部の登録済みアクションを実行できませんでした。",
                    stages);
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                stages.Add(new ControlStage("standby", "failed", MapErrorCode(exception)));
                return Result(
                    $"context:{contextId}:activate",
                    ControlOutcome.PartialFailure,
                    "standby_failed",
                    "コンテキストは有効ですが、アンプをStandbyにできませんでした。",
                    stages);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> DeactivateContextAsync(
        string contextId,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(contextId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var deactivated = _blockers.Deactivate(contextId);
            var stages = new List<ControlStage>
            {
                new("blocker", deactivated ? "completed" : "skipped")
            };
            var actionsSucceeded = !deactivated ||
                await ExecuteActionSequenceAsync(contextId, false, stages, cancellationToken).ConfigureAwait(false);
            return Result(
                $"context:{contextId}:deactivate",
                !deactivated
                    ? ControlOutcome.AlreadySatisfied
                    : actionsSucceeded ? ControlOutcome.Succeeded : ControlOutcome.PartialFailure,
                !deactivated ? "already_inactive" : actionsSucceeded ? "success" : "registered_action_failed",
                !deactivated
                    ? "コンテキストはすでに解除されています。"
                    : actionsSucceeded
                    ? "コンテキストを解除しました。アンプは自動的にONにしません。"
                    : "コンテキストを解除しましたが、一部の登録済みアクションを実行できませんでした。アンプは自動的にONにしません。",
                stages);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> ActivateActivityAsync(
        string activityId,
        CancellationToken cancellationToken = default)
    {
        ValidateActivity(activityId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stages = new List<ControlStage>();
        var changedPower = false;
        try
        {
            if (_blockers.IsBlocked)
            {
                return Result(
                    $"activity:{activityId}:activate",
                    ControlOutcome.Blocked,
                    "blocked_by_context",
                    "アクティビティは電源ON禁止コンテキストによりブロックされました。",
                    stages);
            }

            if (!_settings.Activities.TryGetValue(activityId, out var configuration) ||
                (configuration.InputId is null) == !configuration.SceneNumber.HasValue)
            {
                return Result(
                    $"activity:{activityId}:activate",
                    ControlOutcome.Failed,
                    "configuration_missing",
                    "入力またはSCENEを1つだけ設定してください。",
                    stages);
            }

            ValidateActivityTarget(configuration);
            if (!string.Equals(_deviceManager.Snapshot.MainZone?.Power, "on", StringComparison.OrdinalIgnoreCase))
            {
                await _deviceManager.SetMainPowerAsync(MainPower.On, cancellationToken).ConfigureAwait(false);
                changedPower = true;
                stages.Add(new ControlStage("power-on", "completed"));
                var delay = Math.Clamp(configuration.StartupDelaySeconds, 0, 60);
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
                    stages.Add(new ControlStage("startup-wait", "completed"));
                }
            }
            else
            {
                stages.Add(new ControlStage("power-on", "skipped"));
            }

            if (configuration.InputId is string inputId)
            {
                if (string.Equals(
                        _deviceManager.Snapshot.MainZone?.Input,
                        inputId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    stages.Add(new ControlStage("input", "skipped"));
                }
                else
                {
                    await _deviceManager.SetMainInputAsync(inputId, cancellationToken).ConfigureAwait(false);
                    stages.Add(new ControlStage("input", "completed"));
                }
            }
            else
            {
                await _deviceManager.RecallMainSceneAsync(configuration.SceneNumber!.Value, cancellationToken)
                    .ConfigureAwait(false);
                stages.Add(new ControlStage("scene", "completed"));
            }

            lock (_activitySync)
            {
                _activeActivities.Add(activityId);
            }

            var actionsSucceeded = await ExecuteActionSequenceAsync(activityId, true, stages, cancellationToken)
                .ConfigureAwait(false);

            StateChanged?.Invoke(this, EventArgs.Empty);
            return Result(
                $"activity:{activityId}:activate",
                actionsSucceeded ? ControlOutcome.Succeeded : ControlOutcome.PartialFailure,
                actionsSucceeded ? "success" : "registered_action_failed",
                actionsSucceeded
                    ? "アクティビティを目的状態へ収束させました。"
                    : "アンプ操作は完了しましたが、一部の登録済みアクションを実行できませんでした。",
                stages);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            stages.Add(new ControlStage("operation", "failed", MapErrorCode(exception)));
            return Result(
                $"activity:{activityId}:activate",
                changedPower ? ControlOutcome.PartialFailure : ControlOutcome.Failed,
                MapErrorCode(exception),
                "アクティビティを完了できませんでした。",
                stages);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> DeactivateActivityAsync(
        string activityId,
        CancellationToken cancellationToken = default)
    {
        ValidateActivity(activityId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stages = new List<ControlStage>();
            bool changed;
            lock (_activitySync)
            {
                changed = _activeActivities.Remove(activityId);
            }

            if (changed)
            {
                stages.Add(new ControlStage("activity-state", "completed"));
                var actionsSucceeded = await ExecuteActionSequenceAsync(activityId, false, stages, cancellationToken)
                    .ConfigureAwait(false);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Result(
                    $"activity:{activityId}:deactivate",
                    actionsSucceeded ? ControlOutcome.Succeeded : ControlOutcome.PartialFailure,
                    actionsSucceeded ? "success" : "registered_action_failed",
                    actionsSucceeded
                        ? "アクティビティを解除しました。アンプは自動的にStandbyへ変更しません。"
                        : "アクティビティを解除しましたが、一部の登録済みアクションを実行できませんでした。アンプは変更しません。",
                    stages);
            }

            return Result(
                $"activity:{activityId}:deactivate",
                ControlOutcome.AlreadySatisfied,
                "already_inactive",
                "アクティビティを解除しました。アンプは自動的にStandbyへ変更しません。",
                [new ControlStage("activity-state", "skipped")]);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ActivityStatus> UpdateActivityConfigurationAsync(
        string activityId,
        ActivityConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ValidateActivity(activityId);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.InputId is not null && configuration.SceneNumber.HasValue)
        {
            throw new ArgumentException("Input and scene cannot both be configured.", nameof(configuration));
        }

        var normalized = new ActivityConfiguration
        {
            InputId = string.IsNullOrWhiteSpace(configuration.InputId) ? null : configuration.InputId.Trim(),
            SceneNumber = configuration.SceneNumber,
            StartupDelaySeconds = Math.Clamp(configuration.StartupDelaySeconds, 0, 60)
        };
        if (normalized.InputId is not null || normalized.SceneNumber.HasValue)
        {
            ValidateActivityTarget(normalized);
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActivityConfiguration? previous;
            lock (_settings.SyncRoot)
            {
                _settings.Activities.TryGetValue(activityId, out previous);
                _settings.Activities[activityId] = normalized;
            }
            try
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_settings.SyncRoot)
                {
                    if (previous is null)
                    {
                        _settings.Activities.Remove(activityId);
                    }
                    else
                    {
                        _settings.Activities[activityId] = previous;
                    }
                }

                throw;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            return GetActivities().Single(item =>
                string.Equals(item.Id, activityId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        _blockers.Changed -= State_Changed;
        _deviceManager.SnapshotChanged -= DeviceManager_SnapshotChanged;
        _operationGate.Dispose();
    }

    private void ValidateActivityTarget(ActivityConfiguration configuration)
    {
        var capabilities = _deviceManager.Snapshot.Capabilities
            ?? throw new DeviceUnavailableException("Capabilityを取得できていません。");
        var main = capabilities.FindZone("main")
            ?? throw new CapabilityNotSupportedException("main");
        if (configuration.InputId is string inputId &&
            (!ApplicationScope.IsOperationalInput(inputId) ||
             !main.Inputs.Any(input => string.Equals(input.Id, inputId, StringComparison.OrdinalIgnoreCase))))
        {
            throw new CapabilityNotSupportedException($"main.input.{inputId}");
        }

        if (configuration.SceneNumber is int sceneNumber &&
            (!capabilities.SupportsZoneFunction("main", "scene") ||
             main.SceneCount is not int sceneCount ||
             sceneNumber < 1 ||
             sceneNumber > sceneCount))
        {
            throw new CapabilityNotSupportedException($"main.scene.{sceneNumber}");
        }
    }

    private ControlOperationResult FailureResult(string operationId, Exception exception, bool partial) =>
        Result(
            operationId,
            partial ? ControlOutcome.PartialFailure : ControlOutcome.Failed,
            MapErrorCode(exception),
            "操作を完了できませんでした。",
            [new ControlStage("operation", "failed", MapErrorCode(exception))]);

    private async Task<bool> ExecuteActionSequenceAsync(
        string controlId,
        bool activating,
        ICollection<ControlStage> stages,
        CancellationToken cancellationToken)
    {
        if (_registeredActions is null)
        {
            return true;
        }

        var result = await _registeredActions.ExecuteSequenceAsync(controlId, activating, cancellationToken)
            .ConfigureAwait(false);
        foreach (var action in result.Results)
        {
            stages.Add(new ControlStage(
                $"action:{action.ActionId}",
                action.Succeeded ? "completed" : "failed",
                action.Succeeded ? null : action.Code));
        }

        return result.Succeeded;
    }

    private ControlOperationResult Result(
        string operationId,
        ControlOutcome outcome,
        string code,
        string message,
        IReadOnlyList<ControlStage> stages) => new(
        Guid.NewGuid().ToString("D"),
        operationId,
        outcome,
        code,
        message,
        stages,
        _blockers.Active,
        _deviceManager.Snapshot);

    private static bool IsOperationalFailure(Exception exception) =>
        exception is YamahaException or DeviceUnavailableException or HttpRequestException or
            OperationCanceledException or ArgumentException;

    private static string MapErrorCode(Exception exception) => exception switch
    {
        CapabilityNotSupportedException => "capability_not_supported",
        DeviceUnavailableException or HttpRequestException => "device_unavailable",
        OperationCanceledException => "timeout",
        YamahaApiException => "yamaha_error",
        ArgumentException => "invalid_request",
        _ => "operation_failed"
    };

    private void ValidateContext(string contextId)
    {
        if (!_blockers.Registered.Contains(contextId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(contextId));
        }
    }

    private void ValidateActivity(string activityId)
    {
        lock (_settings.SyncRoot)
        {
            if (!_settings.Activities.ContainsKey(activityId))
            {
                throw new ArgumentOutOfRangeException(nameof(activityId));
            }
        }
    }

    private void State_Changed(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void DeviceManager_SnapshotChanged(object? sender, DeviceSnapshot e) =>
        StateChanged?.Invoke(this, EventArgs.Empty);
}
