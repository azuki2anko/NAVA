namespace RxV4A.Host.Tests;

internal sealed class TestSettingsStore(AppSettings settings) : ISettingsStore
{
    public string SettingsPath => "memory://settings";

    public int SaveCount { get; private set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(settings);

    public Task SaveAsync(AppSettings updatedSettings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
