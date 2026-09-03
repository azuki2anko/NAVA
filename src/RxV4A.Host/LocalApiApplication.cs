using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing;
using RxV4A.Core;

namespace RxV4A.Host;

public static class LocalApiApplication
{
    public const string YamahaHttpClientName = "yamaha";

    public static Task<WebApplication> BuildAsync(
        string[] args,
        CancellationToken cancellationToken = default) =>
        BuildAsync(args, null, cancellationToken);

    public static async Task<WebApplication> BuildAsync(
        string[] args,
        LocalApiExtension? extension,
        CancellationToken cancellationToken = default)
    {
        var settingsStore = new JsonSettingsStore();
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        extension?.ConfigureSettings?.Invoke(settings);
        SettingsValidator.Validate(settings);
        ValidateLoopbackSettings(settings.LocalApi);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(LocalApiApplication).Assembly.FullName
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{settings.LocalApi.Port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddFilter($"System.Net.Http.HttpClient.{YamahaHttpClientName}", LogLevel.None);
        builder.Logging.AddProvider(new JsonFileLoggerProvider(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RXV4A Manager",
                "logs")));

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<ISettingsStore>(settingsStore);
        builder.Services.AddSingleton<IDeviceDiscovery, SsdpDeviceDiscovery>();
        builder.Services.AddSingleton<IPingSubnetDiscovery, PingSubnetDiscovery>();
        builder.Services.AddSingleton<INeighborDeviceDiscovery, WindowsNeighborDeviceDiscovery>();
        builder.Services.AddSingleton<ICapabilityStore, CapabilityStore>();
        builder.Services.AddHttpClient(YamahaHttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 1, 60))
            });
        builder.Services.AddSingleton<IYamahaClientFactory, YamahaClientFactory>();
        builder.Services.AddSingleton<DeviceManager>();
        builder.Services.AddSingleton<IDeviceManager>(services => services.GetRequiredService<DeviceManager>());
        builder.Services.AddHostedService(services => services.GetRequiredService<DeviceManager>());
        builder.Services.AddSingleton<IPowerOnBlockerRegistry>(
            new PowerOnBlockerRegistry(settings.PowerOnBlockers));
        builder.Services.AddSingleton<IRegisteredActionService, RegisteredActionService>();
        builder.Services.AddSingleton<IControlOrchestrator, ControlOrchestrator>();
        extension?.ConfigureServices?.Invoke(builder.Services, settings);
        builder.Services.AddOpenApi("v1");

        var application = builder.Build();
        MapApiEndpoints(application);
        application.MapOpenApi("/openapi/{documentName}.json");
        return application;
    }

    public static void MapApiEndpoints(IEndpointRouteBuilder application)
    {
        var api = application.MapGroup("/api/v1");

        api.MapGet("/health", (IDeviceManager manager) => Results.Ok(new
        {
            status = "ok",
            deviceConnection = manager.Snapshot.ConnectionState.ToString().ToLowerInvariant(),
            timestamp = DateTimeOffset.UtcNow
        }))
            .WithName("GetHealth");

        api.MapGet("/device", (IDeviceManager manager) =>
        {
            var snapshot = manager.Snapshot;
            return Results.Ok(new
            {
                connection = snapshot.ConnectionState.ToString().ToLowerInvariant(),
                model = snapshot.Capabilities?.DeviceInfo.ModelName,
                apiVersion = snapshot.Capabilities?.DeviceInfo.ApiVersion,
                updatedAt = snapshot.UpdatedAt,
                errorCode = snapshot.ErrorCode,
                pingScanConfirmation = snapshot.PingScanConfirmation is null
                    ? null
                    : new
                    {
                        snapshot.PingScanConfirmation.HostCount,
                        snapshot.PingScanConfirmation.BroadestPrefixLength
                    }
            });
        }).WithName("GetDevice");

        api.MapGet("/device/features", (IDeviceManager manager) =>
        {
            var capabilities = manager.Snapshot.Capabilities;
            if (capabilities is null)
            {
                return ApiError(StatusCodes.Status503ServiceUnavailable, "device_unavailable", "対応アンプに接続されていません。");
            }

            return Results.Ok(new
            {
                system = capabilities.Features.System?.Functions,
                zones = capabilities.Features.Zones.Select(zone => new
                {
                    zone.Id,
                    functions = zone.Functions,
                    inputs = zone.Inputs.Select(input => input.Id),
                    soundPrograms = zone.SoundPrograms,
                    surroundDecoderTypes = zone.SurroundDecoderTypes,
                    toneControlModes = zone.ToneControlModes,
                    equalizerModes = zone.EqualizerModes,
                    ranges = zone.Ranges,
                    sceneCount = zone.SceneCount
                })
            });
        }).WithName("GetFeatures");

        api.MapGet("/zones/main/status", (IDeviceManager manager) =>
        {
            var snapshot = manager.Snapshot;
            return snapshot.MainZone is null
                ? ApiError(StatusCodes.Status503ServiceUnavailable, "device_unavailable", "Main Zone状態を取得できません。")
                : Results.Ok(new
                {
                    snapshot.MainZone.Power,
                    snapshot.MainZone.Input,
                    snapshot.MainZone.Volume,
                    snapshot.MainZone.Mute,
                    snapshot.MainZone.SoundProgram,
                    snapshot.MainZone.SurroundDecoderType,
                    snapshot.MainZone.Surround3d,
                    snapshot.MainZone.Direct,
                    snapshot.MainZone.PureDirect,
                    snapshot.MainZone.Enhancer,
                    snapshot.MainZone.ToneControl,
                    snapshot.MainZone.Equalizer,
                    snapshot.MainZone.Balance,
                    snapshot.MainZone.ActualVolume,
                    snapshot.MainZone.Headphone,
                    snapshot.UpdatedAt
                });
        }).WithName("GetMainZoneStatus");

        api.MapPut("/zones/main/power", async (
            PowerRequest request,
            IControlOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            MainPower power;
            if (string.Equals(request.Power, "on", StringComparison.OrdinalIgnoreCase))
            {
                power = MainPower.On;
            }
            else if (string.Equals(request.Power, "standby", StringComparison.OrdinalIgnoreCase))
            {
                power = MainPower.Standby;
            }
            else
            {
                return ApiError(StatusCodes.Status400BadRequest, "invalid_power", "powerは'on'または'standby'で指定してください。");
            }

            if (request.Force)
            {
                return ApiError(
                    StatusCodes.Status403Forbidden,
                    "force_not_authorized",
                    "APIからの強制ONには、今後追加する専用操作権限が必要です。");
            }

            var result = await orchestrator.SetPowerAsync(power, false, cancellationToken).ConfigureAwait(false);
            return ControlResult(result);
        }).WithName("SetMainZonePower");

        api.MapPut("/zones/main/input", (
            InputRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainInputAsync(request.Input, cancellationToken)))
            .WithName("SetMainZoneInput");

        api.MapPut("/zones/main/volume", (
            VolumeRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainVolumeAsync(request.Volume, cancellationToken)))
            .WithName("SetMainZoneVolume");

        api.MapPut("/zones/main/mute", (
            EnabledRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainMuteAsync(request.Enable, cancellationToken)))
            .WithName("SetMainZoneMute");

        api.MapPut("/zones/main/sound-program", (
            SoundProgramRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainSoundProgramAsync(request.Program, cancellationToken)))
            .WithName("SetMainZoneSoundProgram");

        api.MapPut("/zones/main/processing/3d-surround", (
            EnabledRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainSurround3dAsync(request.Enable, cancellationToken)))
            .WithName("SetMainZone3dSurround");

        api.MapPut("/zones/main/processing/direct", (
            EnabledRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainDirectAsync(request.Enable, cancellationToken)))
            .WithName("SetMainZoneDirect");

        api.MapPut("/zones/main/processing/pure-direct", (
            EnabledRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainPureDirectAsync(request.Enable, cancellationToken)))
            .WithName("SetMainZonePureDirect");

        api.MapPut("/zones/main/processing/enhancer", (
            EnabledRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainEnhancerAsync(request.Enable, cancellationToken)))
            .WithName("SetMainZoneEnhancer");

        api.MapPut("/zones/main/tone", (
            ToneControlRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(() => manager.SetMainToneControlAsync(
                new ToneControlSettings(request.Mode, request.Bass, request.Treble),
                cancellationToken)))
            .WithName("SetMainZoneToneControl");

        api.MapPut("/zones/main/equalizer", (
            EqualizerRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(() => manager.SetMainEqualizerAsync(
                new EqualizerSettings(request.Mode, request.Low, request.Mid, request.High),
                cancellationToken)))
            .WithName("SetMainZoneEqualizer");

        api.MapPut("/zones/main/balance", (
            BalanceRequest request,
            IDeviceManager manager,
            CancellationToken cancellationToken) =>
            ExecuteDeviceCommandAsync(
                () => manager.SetMainBalanceAsync(request.Value, cancellationToken)))
            .WithName("SetMainZoneBalance");

        api.MapGet("/activities", (IControlOrchestrator orchestrator) =>
            Results.Ok(orchestrator.GetActivities()))
            .WithName("GetActivities");

        api.MapGet("/activities/{activityId}/status", (string activityId, IControlOrchestrator orchestrator) =>
        {
            var activity = orchestrator.GetActivities().FirstOrDefault(item =>
                string.Equals(item.Id, activityId, StringComparison.OrdinalIgnoreCase));
            return activity is null
                ? ApiError(StatusCodes.Status404NotFound, "activity_not_found", "登録されていないアクティビティです。")
                : Results.Ok(activity);
        }).WithName("GetActivityStatus");

        api.MapPost("/activities/{activityId}:activate", ActivateActivityAsync)
            .WithName("ActivateActivity");
        api.MapPost("/activities/{activityId}:deactivate", DeactivateActivityAsync)
            .WithName("DeactivateActivity");

        api.MapGet("/contexts", (IControlOrchestrator orchestrator) =>
            Results.Ok(new
            {
                activeBlockers = orchestrator.ActiveBlockers,
                contexts = orchestrator.GetContexts()
            }))
            .WithName("GetContexts");

        api.MapGet("/contexts/{contextId}", (string contextId, IControlOrchestrator orchestrator) =>
        {
            var context = orchestrator.GetContexts().FirstOrDefault(item =>
                string.Equals(item.Id, contextId, StringComparison.OrdinalIgnoreCase));
            return context is null
                ? ApiError(StatusCodes.Status404NotFound, "context_not_found", "登録されていないコンテキストです。")
                : Results.Ok(context);
        }).WithName("GetContext");

        api.MapPost("/contexts/{contextId}:activate", ActivateContextAsync)
            .WithName("ActivateContext");
        api.MapPost("/contexts/{contextId}:deactivate", DeactivateContextAsync)
            .WithName("DeactivateContext");

        api.MapGet("/actions", (IRegisteredActionService actions) =>
            Results.Ok(actions.GetRegisteredActions()))
            .WithName("GetRegisteredActions");

        api.MapPost("/actions/{actionId}:execute", async (
            string actionId,
            IRegisteredActionService actions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await actions.ExecuteAsync(actionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(result, statusCode: result.Succeeded
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status422UnprocessableEntity);
            }
            catch (ArgumentOutOfRangeException)
            {
                return ApiError(StatusCodes.Status404NotFound, "registered_action_not_found",
                    "登録されていないアクションIDです。");
            }
        }).WithName("ExecuteRegisteredAction");
    }

    private static async Task<IResult> ActivateActivityAsync(
        string activityId,
        IControlOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        try
        {
            return ControlResult(await orchestrator.ActivateActivityAsync(activityId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiError(StatusCodes.Status404NotFound, "activity_not_found", "登録されていないアクティビティです。");
        }
    }

    private static async Task<IResult> DeactivateActivityAsync(
        string activityId,
        IControlOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        try
        {
            return ControlResult(await orchestrator.DeactivateActivityAsync(activityId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiError(StatusCodes.Status404NotFound, "activity_not_found", "登録されていないアクティビティです。");
        }
    }

    private static async Task<IResult> ActivateContextAsync(
        string contextId,
        IControlOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        try
        {
            return ControlResult(await orchestrator.ActivateContextAsync(contextId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiError(StatusCodes.Status404NotFound, "context_not_found", "登録されていないコンテキストです。");
        }
    }

    private static async Task<IResult> DeactivateContextAsync(
        string contextId,
        IControlOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        try
        {
            return ControlResult(await orchestrator.DeactivateContextAsync(contextId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiError(StatusCodes.Status404NotFound, "context_not_found", "登録されていないコンテキストです。");
        }
    }

    private static IResult ControlResult(ControlOperationResult result)
    {
        var statusCode = result.Outcome switch
        {
            ControlOutcome.Blocked => StatusCodes.Status409Conflict,
            ControlOutcome.Failed when result.Code == "device_unavailable" => StatusCodes.Status503ServiceUnavailable,
            ControlOutcome.Failed when result.Code == "timeout" => StatusCodes.Status504GatewayTimeout,
            ControlOutcome.Failed => StatusCodes.Status422UnprocessableEntity,
            ControlOutcome.PartialFailure => StatusCodes.Status207MultiStatus,
            _ => StatusCodes.Status200OK
        };
        return Results.Json(new
        {
            result.RequestId,
            result.OperationId,
            outcome = result.Outcome.ToString().ToLowerInvariant(),
            result.Code,
            result.Message,
            result.Stages,
            result.ActiveBlockers,
            finalState = new
            {
                connection = result.Snapshot.ConnectionState.ToString().ToLowerInvariant(),
                power = result.Snapshot.MainZone?.Power,
                input = result.Snapshot.MainZone?.Input,
                updatedAt = result.Snapshot.UpdatedAt
            }
        }, statusCode: statusCode);
    }

    private static async Task<IResult> ExecuteDeviceCommandAsync(Func<Task<DeviceSnapshot>> command)
    {
        try
        {
            var snapshot = await command().ConfigureAwait(false);
            return Results.Ok(new
            {
                connection = snapshot.ConnectionState.ToString().ToLowerInvariant(),
                snapshot.MainZone,
                snapshot.UpdatedAt
            });
        }
        catch (DeviceUnavailableException exception)
        {
            return ApiError(StatusCodes.Status503ServiceUnavailable, "device_unavailable", exception.Message);
        }
        catch (CapabilityNotSupportedException exception)
        {
            return ApiError(
                StatusCodes.Status422UnprocessableEntity,
                "capability_not_supported",
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiError(StatusCodes.Status400BadRequest, "invalid_value", exception.Message);
        }
        catch (OperationCanceledException)
        {
            return ApiError(StatusCodes.Status504GatewayTimeout, "timeout", "アンプ操作がタイムアウトしました。");
        }
        catch (YamahaException)
        {
            return ApiError(StatusCodes.Status502BadGateway, "device_error", "アンプが操作を完了できませんでした。");
        }
        catch (HttpRequestException)
        {
            return ApiError(StatusCodes.Status503ServiceUnavailable, "device_unavailable", "アンプと通信できません。");
        }
    }

    private static IResult ApiError(int statusCode, string code, string message) =>
        Results.Json(new { code, message }, statusCode: statusCode);

    private static void ValidateLoopbackSettings(LocalApiSettings settings)
    {
        if (!IPAddress.TryParse(settings.BindAddress, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("MVPのローカルAPIはループバックアドレスだけにバインドできます。");
        }

        if (settings.Port is < 1024 or > 65535)
        {
            throw new InvalidOperationException("ローカルAPIのポート番号が範囲外です。");
        }
    }

    public sealed record PowerRequest(string Power, bool Force = false);

    public sealed record InputRequest(string Input);

    public sealed record VolumeRequest(decimal Volume);

    public sealed record EnabledRequest(bool Enable);

    public sealed record SoundProgramRequest(string Program);

    public sealed record ToneControlRequest(string? Mode, decimal? Bass, decimal? Treble);

    public sealed record EqualizerRequest(string? Mode, decimal? Low, decimal? Mid, decimal? High);

    public sealed record BalanceRequest(decimal Value);
}

public sealed record LocalApiExtension(
    Action<AppSettings>? ConfigureSettings = null,
    Action<IServiceCollection, AppSettings>? ConfigureServices = null);
