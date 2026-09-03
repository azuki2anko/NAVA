using System.Text.Json;
using System.Text.Json.Serialization;

namespace RxV4A.Core;

public abstract record YamahaResponse
{
    [JsonPropertyName("response_code")]
    public int? ResponseCode { get; init; }
}

public sealed record DeviceInfoResponse : YamahaResponse
{
    [JsonPropertyName("model_name")]
    public string? ModelName { get; init; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("api_version")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? ApiVersion { get; init; }

    [JsonPropertyName("system_version")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? SystemVersion { get; init; }

    [JsonPropertyName("category_code")]
    public int? CategoryCode { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Expected a string or number.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static string ReadNumber(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }
}

public sealed record FeaturesResponse : YamahaResponse
{
    [JsonPropertyName("system")]
    public SystemFeatures? System { get; init; }

    [JsonPropertyName("zone")]
    public IReadOnlyList<ZoneFeatures> Zones { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record SystemFeatures
{
    [JsonPropertyName("func_list")]
    public IReadOnlyList<string> Functions { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record ZoneFeatures
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("func_list")]
    public IReadOnlyList<string> Functions { get; init; } = [];

    [JsonPropertyName("input_list")]
    public IReadOnlyList<InputFeature> Inputs { get; init; } = [];

    [JsonPropertyName("sound_program_list")]
    public IReadOnlyList<string> SoundPrograms { get; init; } = [];

    [JsonPropertyName("surr_decoder_type_list")]
    public IReadOnlyList<string> SurroundDecoderTypes { get; init; } = [];

    [JsonPropertyName("tone_control_mode_list")]
    public IReadOnlyList<string> ToneControlModes { get; init; } = [];

    [JsonPropertyName("equalizer_mode_list")]
    public IReadOnlyList<string> EqualizerModes { get; init; } = [];

    [JsonPropertyName("range_step")]
    public IReadOnlyList<RangeStepFeature> Ranges { get; init; } = [];

    [JsonPropertyName("scene_num")]
    public int? SceneCount { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

[JsonConverter(typeof(InputFeatureJsonConverter))]
public sealed record InputFeature
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("distribution_enable")]
    public bool? DistributionEnabled { get; init; }

    [JsonPropertyName("rename_enable")]
    public bool? RenameEnabled { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed class InputFeatureJsonConverter : JsonConverter<InputFeature>
{
    public override InputFeature? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new InputFeature { Id = reader.GetString() ?? string.Empty };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected an input id or input object.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var additionalData = root.EnumerateObject()
            .Where(property => property.Name is not "id" and not "distribution_enable" and not "rename_enable")
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        return new InputFeature
        {
            Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty,
            DistributionEnabled = ReadOptionalBoolean(root, "distribution_enable"),
            RenameEnabled = ReadOptionalBoolean(root, "rename_enable"),
            AdditionalData = additionalData.Count == 0 ? null : additionalData
        };
    }

    public override void Write(Utf8JsonWriter writer, InputFeature value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (value.DistributionEnabled.HasValue)
        {
            writer.WriteBoolean("distribution_enable", value.DistributionEnabled.Value);
        }

        if (value.RenameEnabled.HasValue)
        {
            writer.WriteBoolean("rename_enable", value.RenameEnabled.Value);
        }

        if (value.AdditionalData is not null)
        {
            foreach (var property in value.AdditionalData)
            {
                writer.WritePropertyName(property.Key);
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static bool? ReadOptionalBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
}

public sealed record RangeStepFeature
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("min")]
    public decimal? Minimum { get; init; }

    [JsonPropertyName("max")]
    public decimal? Maximum { get; init; }

    [JsonPropertyName("step")]
    public decimal? Step { get; init; }
}

public sealed record AdvancedFeaturesResponse : YamahaResponse
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Data { get; init; }
}

public sealed record MainZoneStatusResponse : YamahaResponse
{
    [JsonPropertyName("power")]
    public string? Power { get; init; }

    [JsonPropertyName("input")]
    public string? Input { get; init; }

    [JsonPropertyName("volume")]
    public decimal? Volume { get; init; }

    [JsonPropertyName("mute")]
    public bool? Mute { get; init; }

    [JsonPropertyName("sound_program")]
    public string? SoundProgram { get; init; }

    [JsonPropertyName("surr_decoder_type")]
    public string? SurroundDecoderType { get; init; }

    [JsonPropertyName("surround_3d")]
    public bool? Surround3d { get; init; }

    [JsonPropertyName("direct")]
    public bool? Direct { get; init; }

    [JsonPropertyName("pure_direct")]
    public bool? PureDirect { get; init; }

    [JsonPropertyName("enhancer")]
    public bool? Enhancer { get; init; }

    [JsonPropertyName("tone_control")]
    public ToneControlStatus? ToneControl { get; init; }

    [JsonPropertyName("equalizer")]
    public EqualizerStatus? Equalizer { get; init; }

    [JsonPropertyName("balance")]
    public decimal? Balance { get; init; }

    [JsonPropertyName("actual_volume")]
    public ActualVolumeStatus? ActualVolume { get; init; }

    [JsonPropertyName("headphone")]
    public bool? Headphone { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record ToneControlStatus
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("bass")]
    public decimal? Bass { get; init; }

    [JsonPropertyName("treble")]
    public decimal? Treble { get; init; }
}

public sealed record EqualizerStatus
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("low")]
    public decimal? Low { get; init; }

    [JsonPropertyName("mid")]
    public decimal? Mid { get; init; }

    [JsonPropertyName("high")]
    public decimal? High { get; init; }
}

public sealed record ActualVolumeStatus
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("value")]
    public decimal? Value { get; init; }

    [JsonPropertyName("unit")]
    public string? Unit { get; init; }
}

public sealed record ToneControlSettings(string? Mode, decimal? Bass, decimal? Treble);

public sealed record EqualizerSettings(string? Mode, decimal? Low, decimal? Mid, decimal? High);

public sealed record CommandResponse : YamahaResponse;

public enum MainPower
{
    On,
    Standby
}
