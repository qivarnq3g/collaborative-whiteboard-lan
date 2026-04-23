namespace Btl.Whiteboard.Contracts;

public sealed record HelloPayload(string UserId, string UserName, int? ProtocolVersion, string? Password);

public sealed record PresenceUserPayload(string UserId, string UserName);

public sealed record PresenceSnapshotPayload(IReadOnlyList<PresenceUserPayload> Users, string? RoomOwnerId);

public sealed record UserJoinedPayload(string UserId, string UserName);

public sealed record UserLeftPayload(string UserId, string UserName);

public sealed record ChatPayload(string UserName, string Text);

public sealed record StrokeStartPayload(
    string StrokeId,
    double X,
    double Y,
    string UserId,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId);

public sealed record StrokePointPayload(
    string StrokeId,
    double X,
    double Y,
    string UserId,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId);

public sealed record StrokeEndPayload(
    string StrokeId,
    double X,
    double Y,
    string UserId,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId);

public sealed record StrokeCommandPayload(
    string EventType,
    string StrokeId,
    string UserId,
    string UserName,
    string ColorHex,
    double Thickness,
    string LayerId,
    double X,
    double Y);

public sealed record CanvasSnapshotPayload(IReadOnlyList<StrokeCommandPayload> Commands);

public sealed record UndoPayload(string UserName);

public sealed record RedoPayload(string UserName);

public sealed record PingPayload(long SentUnixMilliseconds);

public sealed record PongPayload(long SentUnixMilliseconds);

public sealed record KickPayload(string TargetUserId, string Reason);

public sealed record BanPayload(string TargetUserId, string Reason);

public sealed record UnbanPayload(string TargetUserId, string Reason);

public sealed record BanListPayload(IReadOnlyList<string> UserIds);

public sealed record LockRoomPayload(string Reason);

public sealed record UnlockRoomPayload();

public sealed record ClearCanvasPayload();

public sealed record OwnerAssignedPayload(string OwnerId, string OwnerUserName);

public sealed record ErrorPayload(string Code, string Message);

public sealed record RoomLockedPayload(string Reason);

public sealed record RoomStateInfoPayload(
    bool IsPasswordProtected,
    bool IsRoomLocked,
    string? RoomOwnerId,
    string? RoomOwnerUserName,
    bool IsFirstUserMode);

public sealed record ReserveRoomPasswordPayload(string ClientId, string Password);

public sealed record ReserveRoomPasswordResultPayload(bool Succeeded, string Message, long? ExpiresUnixMilliseconds);

public sealed record UserRenamedPayload(string UserId, string NewUserName);

public sealed record DiscoveryQueryPayload(string AppId, string RoomName, int? ProtocolVersion);

public sealed record DiscoveryResponsePayload(
    string AppId,
    string RoomName,
    string ServerName,
    int TcpPort,
    string ServerId,
    int? ProtocolVersion,
    bool IsPasswordProtected,
    string? OwnerUserName,
    bool IsFirstUserMode = false);
