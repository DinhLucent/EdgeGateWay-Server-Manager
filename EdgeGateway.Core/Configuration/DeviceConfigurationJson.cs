using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeGateway.Core.Configuration;

public static class DeviceConfigurationJson
{
    public static JsonSerializerOptions CreateSerializerOptions(
        bool writeIndented = false,
        bool ignoreNullValues = false
    )
    {
        JsonSerializerOptions options = new()
        {
            DefaultIgnoreCondition = ignoreNullValues
                ? JsonIgnoreCondition.WhenWritingNull
                : JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
