namespace Btl.Whiteboard.Client.Networking;

public sealed class DiscoveredRoom
{
    public string ServerId { get; init; } = string.Empty;

    public string ServerName { get; init; } = string.Empty;

    public string RoomName { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public int TcpPort { get; init; }

    public int? ProtocolVersion { get; init; }

    public string Title => $"{RoomName} - {ServerName}";

    public string Endpoint => $"{Address}:{TcpPort}";

    public override string ToString()
    {
        return $"{Title} ({Endpoint})";
    }
}
