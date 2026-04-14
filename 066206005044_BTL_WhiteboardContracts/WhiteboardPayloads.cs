namespace Btl.Whiteboard.Contracts;

public sealed record HelloPayload(string UserName, int? ProtocolVersion);

public sealed record PresenceUserPayload(string UserId, string UserName);

public sealed record PresenceSnapshotPayload(IReadOnlyList<PresenceUserPayload> Users);

public sealed record UserJoinedPayload(string UserId, string UserName);

public sealed record UserLeftPayload(string UserId, string UserName);

public sealed record ChatPayload(string UserName, string Text);

public sealed record StrokeStartPayload(
    string StrokeId,
    double X,
    double Y,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId);

public sealed record StrokePointPayload(
    string StrokeId,
    double X,
    double Y,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId);

public sealed record StrokeEndPayload(
    string StrokeId,
    double X,
    double Y,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId);

public sealed record StrokeCommandPayload(
    string EventType,
    string StrokeId,
    double X,
    double Y,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId);

public sealed record CanvasSnapshotPayload(IReadOnlyList<StrokeCommandPayload> Commands);

public sealed record UndoPayload(string UserName);

public sealed record RedoPayload(string UserName);

public sealed record PingPayload(long SentUnixMilliseconds);

public sealed record PongPayload(long SentUnixMilliseconds);

public sealed record DiscoveryQueryPayload(string AppId, string RoomName, int? ProtocolVersion);

public sealed record DiscoveryResponsePayload(
    string AppId,
    string RoomName,
    string ServerName,
    int TcpPort,
    string ServerId,
    int? ProtocolVersion);
