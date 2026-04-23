namespace Btl.Whiteboard.Contracts;

public enum WhiteboardMessageType
{
    Hello = 1,
    PresenceSnapshot = 2,
    UserJoined = 3,
    UserLeft = 4,
    Chat = 5,
    StrokeStart = 6,
    StrokePoint = 7,
    StrokeEnd = 8,
    CanvasSnapshot = 9,
    ServerInfo = 10,
    Ping = 11,
    Pong = 12,
    UndoRequest = 13,
    RedoRequest = 14,
    KickUser = 15,
    BanUser = 16,
    LockRoom = 17,
    UnlockRoom = 18,
    ClearCanvas = 19,
    OwnerAssigned = 20,
    Error = 21,
    RoomLocked = 22,
    UnbanUser = 23,
    BanListSnapshot = 24,
    RoomStateInfo = 25,
    ReserveRoomPassword = 26,
    ReserveRoomPasswordResult = 27,
    UserRenamed = 28
}
