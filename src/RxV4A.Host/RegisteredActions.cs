using Microsoft.Extensions.Logging;

namespace RxV4A.Host;

public sealed record RegisteredActionDescriptor(string Id, string DisplayName);

public sealed record RegisteredActionResult(string ActionId, bool Succeeded, string Code, string Message);

public sealed class RegisteredActionException(string code, string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string Code { get; } = code;

    public string SafeMessage { get; } = safeMessage;
}

public sealed record ActionSequenceResult(IReadOnlyList<RegisteredActionResult> Results)
{
    public bool Succeeded => Results.All(item => item.Succeeded);
}

public interface IRegisteredAction
{
    string Id { get; }

    string DisplayName { get; }

    Task ExecuteAsync(CancellationToken cancellationToken = default);
}

public interface IRegisteredActionService
{
    IReadOnlyList<RegisteredActionDescriptor> GetRegisteredActions();

    Task<RegisteredActionResult> ExecuteAsync(string actionId, CancellationToken cancellationToken = default);

    Task<ActionSequenceResult> ExecuteSequenceAsync(
        string controlId,
        bool activating,
        CancellationToken cancellationToken = default);
}

public sealed class RegisteredActionService : IRegisteredActionService
{
    private readonly IReadOnlyDictionary<string, IRegisteredAction> _actions;
    private readonly AppSettings _settings;
    private readonly ILogger<RegisteredActionService> _logger;

    public RegisteredActionService(
        IEnumerable<IRegisteredAction> actions,
        AppSettings settings,
        ILogger<RegisteredActionService> logger)
    {
        var registered = actions.ToArray();
        foreach (var action in registered)
        {
            SettingsValidator.ValidateLogicalId(action.Id, "Registered action");
        }

        _actions = registered.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<RegisteredActionDescriptor> GetRegisteredActions() => _actions.Values
        .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .Select(item => new RegisteredActionDescriptor(item.Id, item.DisplayName))
        .ToArray();

    public async Task<RegisteredActionResult> ExecuteAsync(
        string actionId,
        CancellationToken cancellationToken = default)
    {
        if (!_actions.TryGetValue(actionId, out var action))
        {
            throw new ArgumentOutOfRangeException(nameof(actionId), "Unknown registered action.");
        }

        try
        {
            await action.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("A registered action executed successfully.");
            return new RegisteredActionResult(action.Id, true, "success", "登録済みアクションを実行しました。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RegisteredActionException exception)
        {
            _logger.LogWarning(exception, "A registered action reported a handled failure.");
            return new RegisteredActionResult(action.Id, false, exception.Code, exception.SafeMessage);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "A registered action failed.");
            return new RegisteredActionResult(action.Id, false, "registered_action_failed",
                "登録済みアクションを実行できませんでした。");
        }
    }

    public async Task<ActionSequenceResult> ExecuteSequenceAsync(
        string controlId,
        bool activating,
        CancellationToken cancellationToken = default)
    {
        List<string> actionIds;
        lock (_settings.SyncRoot)
        {
            if (!_settings.ActionBindings.TryGetValue(controlId, out var binding))
            {
                return new ActionSequenceResult([]);
            }

            actionIds = [.. (activating ? binding.OnActivate : binding.OnDeactivate)];
        }

        var results = new List<RegisteredActionResult>(actionIds.Count);
        foreach (var actionId in actionIds)
        {
            try
            {
                results.Add(await ExecuteAsync(actionId, cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentOutOfRangeException)
            {
                results.Add(new RegisteredActionResult(actionId, false, "registered_action_not_found",
                    "登録されていないアクションIDです。"));
            }
        }

        return new ActionSequenceResult(results);
    }
}
