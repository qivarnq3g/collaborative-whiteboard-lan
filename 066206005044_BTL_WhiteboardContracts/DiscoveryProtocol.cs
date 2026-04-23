using System.Text;
using System.Text.Json;

namespace Btl.Whiteboard.Contracts;

public static class DiscoveryProtocol
{
    public const string ApplicationId = "BTL_WHITEBOARD_LAN";
    public const int CurrentProtocolVersion = 5;
    public const int DefaultDiscoveryPort = 49001;
    public const int DefaultTcpPort = 49000;
    public const string DefaultRoomName = "BTL-LAN-ROOM";

    public static byte[] ToDatagram<T>(T payload)
    {
        string json = JsonSerializer.Serialize(payload, ProtocolJson.SerializerOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    public static bool TryParseDatagram<T>(byte[] datagram, out T? payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<T>(datagram, ProtocolJson.SerializerOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }
}
