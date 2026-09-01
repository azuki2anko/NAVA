using Microsoft.Extensions.Logging.Abstractions;

namespace RxV4A.Host.Tests;

public sealed class RegisteredActionServiceTests
{
    [Fact]
    public async Task RegisteredAction_CanBeListedExecutedAndUsedInSequence()
    {
        var action = new FakeAction("sample-action");
        var settings = new AppSettings
        {
            Activities = new Dictionary<string, ActivityConfiguration>(StringComparer.OrdinalIgnoreCase)
            {
                ["sample-activity"] = new()
            },
            ActionBindings = new Dictionary<string, ActionSequenceBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["sample-activity"] = new() { OnActivate = [action.Id] }
            }
        };
        var service = new RegisteredActionService(
            [action],
            settings,
            NullLogger<RegisteredActionService>.Instance);

        var descriptors = service.GetRegisteredActions();
        var result = await service.ExecuteSequenceAsync("sample-activity", true);

        Assert.Equal(action.Id, Assert.Single(descriptors).Id);
        Assert.True(result.Succeeded);
        Assert.Equal(1, action.ExecutionCount);
    }

    [Fact]
    public async Task UnknownAction_IsRejectedWithoutExecutingAnything()
    {
        var service = new RegisteredActionService(
            [],
            new AppSettings(),
            NullLogger<RegisteredActionService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExecuteAsync("unknown"));
    }

    [Fact]
    public async Task HandledFailure_PreservesSafeMachineReadableCode()
    {
        var action = new FailingAction();
        var service = new RegisteredActionService(
            [action],
            new AppSettings(),
            NullLogger<RegisteredActionService>.Instance);

        var result = await service.ExecuteAsync(action.Id);

        Assert.False(result.Succeeded);
        Assert.Equal("dependency_unavailable", result.Code);
        Assert.Equal("依存機能を利用できません。", result.Message);
    }

    private sealed class FakeAction(string id) : IRegisteredAction
    {
        public string Id { get; } = id;

        public string DisplayName => "Sample action";

        public int ExecutionCount { get; private set; }

        public Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingAction : IRegisteredAction
    {
        public string Id => "failing-action";

        public string DisplayName => "Failing action";

        public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
            throw new RegisteredActionException("dependency_unavailable", "依存機能を利用できません。");
    }
}
