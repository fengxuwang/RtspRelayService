using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace ActivityRecordingService.Api;

public static class RecordingApiJson
{
    public static IServiceCollection AddRecordingApiJsonOptions(
        this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new ChinaStandardTimeJsonConverter());
            options.SerializerOptions.Converters.Add(new NullableChinaStandardTimeJsonConverter());
        });
}

public sealed class ChinaStandardTimeJsonConverter : JsonConverter<DateTimeOffset>
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    internal const string SerializationFormat = "yyyy-MM-dd HH:mm:ss";

    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(
            value.ToOffset(Offset).ToString(SerializationFormat, CultureInfo.InvariantCulture));
}

public sealed class NullableChinaStandardTimeJsonConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType is JsonTokenType.Null
            ? null
            : new ChinaStandardTimeJsonConverter().Read(ref reader, typeToConvert, options);

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset? value,
        JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(
                value.Value.ToOffset(ChinaStandardTimeJsonConverter.Offset)
                    .ToString(
                        ChinaStandardTimeJsonConverter.SerializationFormat,
                        CultureInfo.InvariantCulture));
        else
            writer.WriteNullValue();
    }
}
