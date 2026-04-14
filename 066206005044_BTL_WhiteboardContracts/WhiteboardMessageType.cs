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
    RedoRequest = 14
}
