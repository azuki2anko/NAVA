using System.Net.Http.Json;
using System.Text.Json;

namespace RxV4A.Core;

public interface IYamahaClient
{
    Task<DeviceInfoResponse> GetDeviceInfoAsync(CancellationToken cancellationToken);

    Task<FeaturesResponse> GetFeaturesAsync(CancellationToken cancellationToken);

    Task<AdvancedFeaturesResponse> GetAdvancedFeaturesAsync(CancellationToken cancellationToken);

    Task<MainZoneStatusResponse> GetMainZoneStatusAsync(CancellationToken cancellationToken);

    Task SetMainPowerAsync(MainPower power, CancellationToken cancellationToken);

    Task SetMainInputAsync(string inputId, CancellationToken cancellationToken);

    Task SetMainVolumeAsync(decimal volume, CancellationToken cancellationToken);

    Task SetMainMuteAsync(bool enabled, CancellationToken cancellationToken);

    Task SetMainSoundProgramAsync(string programId, CancellationToken cancellationToken);

    Task SetMainSurround3dAsync(bool enabled, CancellationToken cancellationToken);

    Task SetMainDirectAsync(bool enabled, CancellationToken cancellationToken);

    Task SetMainPureDirectAsync(bool enabled, CancellationToken cancellationToken);

    Task SetMainEnhancerAsync(bool enabled, CancellationToken cancellationToken);

    Task SetMainToneControlAsync(ToneControlSettings settings, CancellationToken cancellationToken);

    Task SetMainEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken);

    Task SetMainBalanceAsync(decimal value, CancellationToken cancellationToken);

    Task RecallMainSceneAsync(int sceneNumber, CancellationToken cancellationToken);
}

public sealed class YamahaClient : IYamahaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly int? _eventPort;

    public YamahaClient(HttpClient httpClient, string host, int? eventPort = null)
    {
        _httpClient = httpClient;
        _baseUri = CreateBaseUri(host);
        _eventPort = eventPort is > 0 and <= 65535 ? eventPort : null;
    }

    public Task<DeviceInfoResponse> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
        GetAsync<DeviceInfoResponse>("system/getDeviceInfo", cancellationToken);

    public Task<FeaturesResponse> GetFeaturesAsync(CancellationToken cancellationToken) =>
        GetAsync<FeaturesResponse>("system/getFeatures", cancellationToken);

    public Task<AdvancedFeaturesResponse> GetAdvancedFeaturesAsync(CancellationToken cancellationToken) =>
        GetAsync<AdvancedFeaturesResponse>("system/getAdvancedFeatures", cancellationToken);

    public Task<MainZoneStatusResponse> GetMainZoneStatusAsync(CancellationToken cancellationToken) =>
        GetAsync<MainZoneStatusResponse>("main/getStatus", cancellationToken);

    public async Task SetMainPowerAsync(MainPower power, CancellationToken cancellationToken)
    {
        var value = power == MainPower.On ? "on" : "standby";
        await GetAsync<CommandResponse>($"main/setPower?power={value}", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMainInputAsync(string inputId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inputId))
        {
            throw new ArgumentException("An input id is required.", nameof(inputId));
        }

        var value = Uri.EscapeDataString(inputId.Trim());
        await GetAsync<CommandResponse>($"main/setInput?input={value}", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMainVolumeAsync(decimal volume, CancellationToken cancellationToken) =>
        await GetAsync<CommandResponse>(
            $"main/setVolume?volume={FormatNumber(volume)}",
            cancellationToken).ConfigureAwait(false);

    public async Task SetMainMuteAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetMainBooleanAsync("setMute", enabled, cancellationToken).ConfigureAwait(false);

    public async Task SetMainSoundProgramAsync(string programId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(programId))
        {
            throw new ArgumentException("A sound program id is required.", nameof(programId));
        }

        await GetAsync<CommandResponse>(
            $"main/setSoundProgram?program={Uri.EscapeDataString(programId.Trim())}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMainSurround3dAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetMainBooleanAsync("set3dSurround", enabled, cancellationToken).ConfigureAwait(false);

    public async Task SetMainDirectAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetMainBooleanAsync("setDirect", enabled, cancellationToken).ConfigureAwait(false);

    public async Task SetMainPureDirectAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetMainBooleanAsync("setPureDirect", enabled, cancellationToken).ConfigureAwait(false);

    public async Task SetMainEnhancerAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetMainBooleanAsync("setEnhancer", enabled, cancellationToken).ConfigureAwait(false);

    public async Task SetMainToneControlAsync(ToneControlSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var query = BuildOptionalSettingsQuery(
            ("mode", settings.Mode),
            ("bass", FormatOptionalNumber(settings.Bass)),
            ("treble", FormatOptionalNumber(settings.Treble)));
        await GetAsync<CommandResponse>($"main/setToneControl?{query}", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMainEqualizerAsync(EqualizerSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var query = BuildOptionalSettingsQuery(
            ("mode", settings.Mode),
            ("low", FormatOptionalNumber(settings.Low)),
            ("mid", FormatOptionalNumber(settings.Mid)),
            ("high", FormatOptionalNumber(settings.High)));
        await GetAsync<CommandResponse>($"main/setEqualizer?{query}", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMainBalanceAsync(decimal value, CancellationToken cancellationToken) =>
        await GetAsync<CommandResponse>(
            $"main/setBalance?value={FormatNumber(value)}",
            cancellationToken).ConfigureAwait(false);

    public async Task RecallMainSceneAsync(int sceneNumber, CancellationToken cancellationToken)
    {
        if (sceneNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sceneNumber));
        }

        await GetAsync<CommandResponse>($"main/recallScene?num={sceneNumber}", cancellationToken)
            .ConfigureAwait(false);
    }

    public static Uri CreateBaseUri(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("A device host is required.", nameof(host));
        }

        var candidate = host.Contains("://", StringComparison.Ordinal)
            ? host.Trim()
            : $"http://{host.Trim()}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            throw new ArgumentException("Host must be an HTTP host name or IP address without a path.", nameof(host));
        }

        return new UriBuilder(Uri.UriSchemeHttp, uri.Host, uri.IsDefaultPort ? 80 : uri.Port).Uri;
    }

    private async Task SetMainBooleanAsync(
        string operation,
        bool enabled,
        CancellationToken cancellationToken) =>
        await GetAsync<CommandResponse>(
            $"main/{operation}?enable={enabled.ToString().ToLowerInvariant()}",
            cancellationToken).ConfigureAwait(false);

    private static string FormatNumber(decimal value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string? FormatOptionalNumber(decimal? value) =>
        value.HasValue ? FormatNumber(value.Value) : null;

    private static string BuildOptionalSettingsQuery(params (string Name, string? Value)[] parameters)
    {
        var query = string.Join("&", parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(parameter.Value!.Trim())}"));
        return query.Length > 0
            ? query
            : throw new ArgumentException("At least one setting is required.", nameof(parameters));
    }

    private async Task<TResponse> GetAsync<TResponse>(string relativePath, CancellationToken cancellationToken)
        where TResponse : YamahaResponse
    {
        var requestUri = new Uri(_baseUri, $"YamahaExtendedControl/v1/{relativePath}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("X-AppName", "NAVA/1.0");
        if (_eventPort is int port)
        {
            request.Headers.TryAddWithoutValidation("X-AppPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new YamahaProtocolException($"Yamaha endpoint returned HTTP {(int)response.StatusCode}.");
        }

        TResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new YamahaProtocolException("Yamaha endpoint returned invalid JSON.", exception);
        }

        if (payload is null)
        {
            throw new YamahaProtocolException("Yamaha endpoint returned an empty response.");
        }

        if (payload.ResponseCode is null)
        {
            throw new YamahaProtocolException("Yamaha endpoint response did not include response_code.");
        }

        if (payload.ResponseCode != 0)
        {
            throw new YamahaApiException(payload.ResponseCode.Value);
        }

        return payload;
    }
}
