using System.Text.Json;
using System.Text.Json.Serialization;

namespace Btl.Whiteboard.Contracts;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static ProtocolJson()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static JsonElement ToJsonElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, SerializerOptions);
    }

    public static T? Deserialize<T>(JsonElement element)
    {
        return JsonSerializer.Deserialize<T>(element.GetRawText(), SerializerOptions);
    }
}
