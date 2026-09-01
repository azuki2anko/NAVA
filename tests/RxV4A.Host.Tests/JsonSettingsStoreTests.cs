namespace RxV4A.Host.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task Settings_RoundTripInLocalApplicationDirectoryShape()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(root);
            var expected = new AppSettings
            {
                ManualHost = "receiver.local",
                PreferredDeviceId = "example-device-id",
                ShowCompactPowerStatus = true,
                RequestTimeoutSeconds = 7,
                LocalApi = new LocalApiSettings { BindAddress = "127.0.0.1", Port = 55274 },
                CompactWindow = new CompactWindowPlacementSettings
                {
                    Left = 120,
                    Top = 80,
                    Width = 240,
                    Height = 76,
                    VirtualScreenLeft = 0,
                    VirtualScreenTop = 0,
                    VirtualScreenWidth = 1920,
                    VirtualScreenHeight = 1080
                }
            };

            await store.SaveAsync(expected, CancellationToken.None);
            var actual = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(expected.ManualHost, actual.ManualHost);
            Assert.Equal(expected.PreferredDeviceId, actual.PreferredDeviceId);
            Assert.True(actual.ShowCompactPowerStatus);
            Assert.Equal(7, actual.RequestTimeoutSeconds);
            Assert.Equal(55274, actual.LocalApi.Port);
            Assert.Equal(120, actual.CompactWindow.Left);
            Assert.Equal(80, actual.CompactWindow.Top);
            Assert.Equal(240, actual.CompactWindow.Width);
            Assert.Equal(76, actual.CompactWindow.Height);
            Assert.Equal(1920, actual.CompactWindow.VirtualScreenWidth);
            Assert.Equal(1080, actual.CompactWindow.VirtualScreenHeight);
            Assert.StartsWith(root, store.SettingsPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task CorruptSettings_ReturnSafePublicDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
            await File.WriteAllTextAsync(store.SettingsPath, "{ invalid json", CancellationToken.None);

            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Empty(loaded.Activities);
            Assert.Empty(loaded.PowerOnBlockers);
            Assert.Equal(HotkeyActionIds.PowerOn, loaded.GlobalHotkeys[0].ActionId);
            Assert.Equal(HotkeyActionIds.PowerStandby, loaded.GlobalHotkeys[1].ActionId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ConcurrentSaves_AreSerializedAndLeaveValidJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(root);
            var settings = new AppSettings();
            var saves = Enumerable.Range(1, 8).Select(async value =>
            {
                lock (settings.SyncRoot)
                {
                    settings.PollIntervalSeconds = value;
                }

                await store.SaveAsync(settings, CancellationToken.None);
            });

            await Task.WhenAll(saves);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.InRange(loaded.PollIntervalSeconds, 1, 8);
            Assert.Empty(loaded.Activities);
            Assert.Empty(loaded.PowerOnBlockers);
            Assert.Equal(HotkeyActionIds.PowerOn, loaded.GlobalHotkeys[0].ActionId);
            Assert.Equal(HotkeyActionIds.PowerStandby, loaded.GlobalHotkeys[1].ActionId);
            Assert.All(loaded.GlobalHotkeys.Skip(2), item => Assert.Null(item.ActionId));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
