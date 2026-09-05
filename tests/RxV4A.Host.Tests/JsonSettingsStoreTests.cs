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
                MinimizeToTray = false,
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
            Assert.False(actual.MinimizeToTray);
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
            Assert.True(loaded.MinimizeToTray);
            Assert.Equal(HotkeyActionIds.PowerToggle, loaded.GlobalHotkeys[0].ActionId);
            Assert.Equal(HotkeyActionIds.MuteToggle, loaded.GlobalHotkeys[1].ActionId);
            Assert.Equal(HotkeyActionIds.VolumeDown, loaded.GlobalHotkeys[2].ActionId);
            Assert.Equal(HotkeyActionIds.VolumeUp, loaded.GlobalHotkeys[3].ActionId);
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
    public async Task LegacyProductDirectory_IsLoadedAndMigratedToCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var legacyPath = Path.Combine(root, "Yamaha AV Manager", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            await File.WriteAllTextAsync(legacyPath, """
                {
                  "manualHost": "legacy-receiver.local",
                  "pollIntervalSeconds": 9
                }
                """, CancellationToken.None);

            var store = new JsonSettingsStore(root);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal("legacy-receiver.local", loaded.ManualHost);
            Assert.Equal(9, loaded.PollIntervalSeconds);
            Assert.True(File.Exists(store.SettingsPath));
            Assert.Contains("NAVA", store.SettingsPath, StringComparison.Ordinal);
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
            Assert.Equal(HotkeyActionIds.PowerToggle, loaded.GlobalHotkeys[0].ActionId);
            Assert.Equal(HotkeyActionIds.MuteToggle, loaded.GlobalHotkeys[1].ActionId);
            Assert.Equal(HotkeyActionIds.VolumeDown, loaded.GlobalHotkeys[2].ActionId);
            Assert.Equal(HotkeyActionIds.VolumeUp, loaded.GlobalHotkeys[3].ActionId);
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
    public async Task ExtendedHotkey_RoundTripsKeyActionAndVolumeAmount()
    {
        var root = Path.Combine(Path.GetTempPath(), "rxv4a-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(root);
            var settings = new AppSettings
            {
                GlobalHotkeys =
                [
                    new GlobalHotkeyBinding
                    {
                        Gesture = new HotkeyGesture { Ctrl = true, Shift = true, VirtualKey = 0x56 },
                        ActionId = HotkeyActionIds.VolumeUp,
                        Amount = 2.5m
                    }
                ]
            };

            await store.SaveAsync(settings, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);

            var binding = Assert.Single(loaded.GlobalHotkeys);
            Assert.Equal(0x56, binding.Gesture.VirtualKey);
            Assert.Equal("Ctrl+Shift+V", binding.Gesture.DisplayName);
            Assert.Equal(HotkeyActionIds.VolumeUp, binding.ActionId);
            Assert.Equal(2.5m, binding.Amount);
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
    public void VolumeHotkey_RequiresPositiveAmount()
    {
        var settings = new AppSettings
        {
            GlobalHotkeys =
            [
                new GlobalHotkeyBinding
                {
                    Gesture = new HotkeyGesture { Alt = true, VirtualKey = 0x70 },
                    ActionId = HotkeyActionIds.VolumeDown
                }
            ]
        };

        Assert.Throws<InvalidDataException>(() => SettingsValidator.ValidateGlobalHotkeys(settings));
    }

    [Fact]
    public void HotkeyDefaults_AssignPowerMuteAndVolumeActions()
    {
        var defaults = GlobalHotkeyDefaults.Create();

        Assert.Collection(defaults,
            item => Assert.Equal(HotkeyActionIds.PowerToggle, item.ActionId),
            item => Assert.Equal(HotkeyActionIds.MuteToggle, item.ActionId),
            item =>
            {
                Assert.Equal(HotkeyActionIds.VolumeDown, item.ActionId);
                Assert.Equal(1m, item.Amount);
            },
            item =>
            {
                Assert.Equal(HotkeyActionIds.VolumeUp, item.ActionId);
                Assert.Equal(1m, item.Amount);
            });
    }
}
