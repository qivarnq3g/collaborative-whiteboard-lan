using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Btl.Whiteboard.Contracts;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private async void CanvasHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to a room before drawing.");
            return;
        }

        var currentPointOnHost = e.GetCurrentPoint(CanvasHost);
        bool isMousePointer = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
        if (isMousePointer && !currentPointOnHost.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point point = NormalizeCanvasPoint(e.GetCurrentPoint(DrawingCanvas).Position);
        UpdateCanvasPointerText(point);

        LayerState activeLayer = GetActiveLayerState();
        if (!activeLayer.IsVisible)
        {
            SetConnectionState("Error", $"{activeLayer.Name} is hidden. Enable visibility before drawing.");
            return;
        }

        if (activeLayer.IsLocked)
        {
            SetConnectionState("Error", $"{activeLayer.Name} is locked. Unlock it before drawing.");
            return;
        }

        activeStrokeLayerId = activeLayer.LayerId;

        if (IsShapeTool(selectedDrawingTool))
        {
            isDrawing = true;
            isShapePreviewActive = true;
            shapeStartPoint = point;
            activeStrokeId = null;
            pendingStrokePoints.Clear();
            activeStrokeStartSendTask = null;
            hasSentActiveStrokeStart = false;
            strokeSendTimer.Stop();
            RemoveShapePreview();
            shapePreviewShape = CreateShapePreview(selectedDrawingTool);
            if (shapePreviewShape is not null)
            {
                DrawingCanvas.Children.Add(shapePreviewShape);
                UpdateShapePreview(shapeStartPoint, point);
            }

            CanvasHost.CapturePointer(e.Pointer);
            return;
        }

        isDrawing = true;
        activeStrokeId = Guid.NewGuid().ToString("N");
        TrackLocalStroke(activeStrokeId);
        activeStrokeColorHex = GetEffectiveStrokeColorHex();
        activeStrokeThickness = GetEffectiveStrokeThickness();
        pendingStrokePoints.Clear();
        activeStrokeStartSendTask = null;
        hasSentActiveStrokeStart = false;
        hasLastSentStrokePoint = true;
        lastSentStrokePoint = point;
        CanvasHost.CapturePointer(e.Pointer);
        strokeSendTimer.Stop();

        DrawStrokeStart(new StrokeStartPayload(
            activeStrokeId,
            point.X,
            point.Y,
            GetSafeUserName(),
            activeStrokeColorHex,
            activeStrokeThickness,
            activeStrokeLayerId));

        activeStrokeStartSendTask = SafeSendStrokeAsync(() => session.SendStrokeStartAsync(
            activeStrokeId,
            point.X,
            point.Y,
            GetSafeUserName(),
            activeStrokeColorHex,
            activeStrokeThickness,
            activeStrokeLayerId,
            CancellationToken.None));

        bool strokeStartSent = await EnsureActiveStrokeStartSentAsync();
        if (!strokeStartSent)
        {
            isDrawing = false;
            activeStrokeId = null;
            pendingStrokePoints.Clear();
            CanvasHost.ReleasePointerCapture(e.Pointer);
            return;
        }

        if (isDrawing && !string.IsNullOrWhiteSpace(activeStrokeId))
        {
            strokeSendTimer.Start();
        }
    }

    private void CanvasHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        IReadOnlyList<Point> sampledPoints = GetPointerSamplePoints(e);
        Point point = sampledPoints[^1];
        UpdateCanvasPointerText(point);

        if (!session.IsConnected || !isDrawing)
        {
            return;
        }

        bool isMousePointer = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
        bool isLeftButtonPressed = e.GetCurrentPoint(CanvasHost).Properties.IsLeftButtonPressed;
        if (isMousePointer && !isLeftButtonPressed)
        {
            CanvasHost_PointerReleased(sender, e);
            return;
        }

        if (isShapePreviewActive)
        {
            UpdateShapePreview(shapeStartPoint, point);
            return;
        }

        if (string.IsNullOrWhiteSpace(activeStrokeId))
        {
            return;
        }

        string strokeId = activeStrokeId;
        string userName = GetSafeUserName();

        foreach (Point sampledPoint in sampledPoints)
        {
            DrawStrokePoint(new StrokePointPayload(
                strokeId,
                sampledPoint.X,
                sampledPoint.Y,
                userName,
                activeStrokeColorHex,
                activeStrokeThickness,
                activeStrokeLayerId));

            pendingStrokePoints.Enqueue(sampledPoint);
        }
    }

    private async void CanvasHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!session.IsConnected || !isDrawing)
        {
            return;
        }

        Point point = NormalizeCanvasPoint(e.GetCurrentPoint(DrawingCanvas).Position);
        UpdateCanvasPointerText(point);

        if (isShapePreviewActive)
        {
            isDrawing = false;
            isShapePreviewActive = false;
            CanvasHost.ReleasePointerCapture(e.Pointer);
            RemoveShapePreview();

            IReadOnlyList<Point> shapePoints = BuildShapeStrokePoints(shapeStartPoint, point, selectedDrawingTool);
            await SendShapeStrokeAsync(shapePoints).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(activeStrokeId))
        {
            return;
        }

        string strokeId = activeStrokeId;
        string userName = GetSafeUserName();

        isDrawing = false;
        CanvasHost.ReleasePointerCapture(e.Pointer);
        strokeSendTimer.Stop();

        bool strokeStartSent = await EnsureActiveStrokeStartSentAsync().ConfigureAwait(false);
        if (!strokeStartSent)
        {
            activeStrokeId = null;
            pendingStrokePoints.Clear();
            activeStrokeStartSendTask = null;
            hasSentActiveStrokeStart = false;
            return;
        }

        DrawStrokePoint(new StrokePointPayload(
            strokeId,
            point.X,
            point.Y,
            userName,
            activeStrokeColorHex,
            activeStrokeThickness,
            activeStrokeLayerId));
        pendingStrokePoints.Enqueue(point);
        await SendPendingStrokePointAsync(forceSend: true).ConfigureAwait(false);

        activeStrokeId = null;
        pendingStrokePoints.Clear();
        activeStrokeStartSendTask = null;
        hasSentActiveStrokeStart = false;

        await SafeSendStrokeAsync(() => session.SendStrokeEndAsync(
            strokeId,
            point.X,
            point.Y,
            GetSafeUserName(),
            activeStrokeColorHex,
            activeStrokeThickness,
            activeStrokeLayerId,
            CancellationToken.None)).ConfigureAwait(false);

        activeStrokes.Remove(strokeId);
        strokeLayers.Remove(strokeId);

        activeStrokeColorHex = GetEffectiveStrokeColorHex();
        activeStrokeThickness = GetEffectiveStrokeThickness();
        activeStrokeLayerId = activeLayerId;
    }

    private void CanvasHost_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CanvasHost.ReleasePointerCapture(e.Pointer);
        isDrawing = false;
        isShapePreviewActive = false;
        RemoveShapePreview();
        activeStrokeId = null;
        activeStrokeColorHex = GetEffectiveStrokeColorHex();
        activeStrokeThickness = GetEffectiveStrokeThickness();
        activeStrokeLayerId = activeLayerId;
        pendingStrokePoints.Clear();
        activeStrokeStartSendTask = null;
        hasSentActiveStrokeStart = false;
        strokeSendTimer.Stop();
    }

    private Point NormalizeCanvasPoint(Point point)
    {
        double maxX = Math.Max(0, DrawingCanvas.ActualWidth - 1);
        double maxY = Math.Max(0, DrawingCanvas.ActualHeight - 1);
        return new Point(
            Math.Clamp(point.X, 0, maxX),
            Math.Clamp(point.Y, 0, maxY));
    }

    private IReadOnlyList<Point> GetPointerSamplePoints(PointerRoutedEventArgs e)
    {
        var intermediatePoints = e.GetIntermediatePoints(DrawingCanvas);
        if (intermediatePoints.Count == 0)
        {
            return new[] { NormalizeCanvasPoint(e.GetCurrentPoint(DrawingCanvas).Position) };
        }

        bool isDescendingByTimestamp = intermediatePoints.Count > 1 &&
            intermediatePoints[0].Timestamp > intermediatePoints[intermediatePoints.Count - 1].Timestamp;

        List<Point> points = new(intermediatePoints.Count);

        if (isDescendingByTimestamp)
        {
            for (int index = intermediatePoints.Count - 1; index >= 0; index--)
            {
                points.Add(NormalizeCanvasPoint(intermediatePoints[index].Position));
            }
        }
        else
        {
            for (int index = 0; index < intermediatePoints.Count; index++)
            {
                points.Add(NormalizeCanvasPoint(intermediatePoints[index].Position));
            }
        }

        return points;
    }

    private async void StrokeSendTimer_Tick(object? sender, object e)
    {
        await SendPendingStrokePointAsync(forceSend: false).ConfigureAwait(false);
    }

    private async Task SendPendingStrokePointAsync(bool forceSend)
    {
        if (!session.IsConnected ||
            string.IsNullOrWhiteSpace(activeStrokeId) ||
            pendingStrokePoints.Count == 0 ||
            isStrokeSendInFlight)
        {
            return;
        }

        if (!hasSentActiveStrokeStart)
        {
            bool strokeStartSent = await EnsureActiveStrokeStartSentAsync().ConfigureAwait(false);
            if (!strokeStartSent)
            {
                pendingStrokePoints.Clear();
                return;
            }
        }

        string strokeId = activeStrokeId;
        string userName = GetSafeUserName();
        int sendLimit = forceSend ? int.MaxValue : MaxStrokePointsPerFlush;
        int sentCount = 0;
        isStrokeSendInFlight = true;

        try
        {
            while (pendingStrokePoints.Count > 0 && sentCount < sendLimit)
            {
                Point point = pendingStrokePoints.Dequeue();
                if (!forceSend &&
                    hasLastSentStrokePoint &&
                    CalculateDistance(lastSentStrokePoint, point) < MinStrokePointDistance)
                {
                    continue;
                }

                await session.SendStrokePointAsync(
                    strokeId,
                    point.X,
                    point.Y,
                    userName,
                    activeStrokeColorHex,
                    activeStrokeThickness,
                    activeStrokeLayerId,
                    CancellationToken.None).ConfigureAwait(false);

                lastSentStrokePoint = point;
                hasLastSentStrokePoint = true;
                sentCount++;
            }
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Stroke send failed: {exception.Message}");
        }
        finally
        {
            isStrokeSendInFlight = false;
        }
    }

    private async Task<bool> EnsureActiveStrokeStartSentAsync()
    {
        if (hasSentActiveStrokeStart)
        {
            return true;
        }

        Task<bool>? strokeStartTask = activeStrokeStartSendTask;
        if (strokeStartTask is null)
        {
            return false;
        }

        bool sent = await strokeStartTask.ConfigureAwait(false);
        hasSentActiveStrokeStart = sent;
        return sent;
    }

    private static double CalculateDistance(Point p1, Point p2)
    {
        double dx = p1.X - p2.X;
        double dy = p1.Y - p2.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private async Task<bool> SafeSendStrokeAsync(Func<Task> sendAction)
    {
        try
        {
            await sendAction().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Stroke send failed: {exception.Message}");
            return false;
        }
    }

    private void Session_ConnectionStateChanged(object? sender, string state)
    {
        if (isWindowClosed)
        {
            return;
        }

        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (isWindowClosed)
                {
                    return;
                }

                if (string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase))
                {
                    SetConnectionState("Connected", string.IsNullOrWhiteSpace(connectedEndpoint) ? "Connected" : $"Connected to {connectedEndpoint}");
                    return;
                }

                if (state.StartsWith("Connection error", StringComparison.OrdinalIgnoreCase))
                {
                    ResetLocalDrawingState();
                    SetConnectionState("Error", state);
                    return;
                }

                if (state.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase))
                {
                    SetConnectionState("Connecting", state);
                    return;
                }

                if (string.Equals(state, "Disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    SetConnectionState("Disconnected", "Connection closed.");
                    onlineUsers.Clear();
                    UpdateOnlineUserVisuals();
                    ResetLocalDrawingState();
                }
            });
        }
        catch (Exception)
        {
        }
    }

    private void Session_EnvelopeReceived(object? sender, WhiteboardEnvelope envelope)
    {
        if (isWindowClosed)
        {
            return;
        }

        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!isWindowClosed)
                {
                    HandleEnvelope(envelope);
                }
            });
        }
        catch (Exception)
        {
        }
    }

    private void HandleEnvelope(WhiteboardEnvelope envelope)
    {
        try
        {
            switch (envelope.Type)
            {
                case WhiteboardMessageType.Chat:
                    ChatPayload chatPayload = envelope.ReadPayload<ChatPayload>();
                    AppendChatLine(
                        $"[{envelope.TimestampUtc.ToLocalTime():HH:mm:ss}] {chatPayload.UserName}: {chatPayload.Text}",
                        BuildChatMessageKey(envelope));
                    break;

                case WhiteboardMessageType.PresenceSnapshot:
                    PresenceSnapshotPayload snapshotPayload = envelope.ReadPayload<PresenceSnapshotPayload>();
                    onlineUsers.Clear();
                    foreach (PresenceUserPayload user in snapshotPayload.Users)
                    {
                        onlineUsers.Add(user.UserName);
                    }

                    UpdateOnlineUserVisuals();
                    break;

                case WhiteboardMessageType.UserJoined:
                    UserJoinedPayload joinedPayload = envelope.ReadPayload<UserJoinedPayload>();
                    AppendChatLine(
                        $"[System] {joinedPayload.UserName} joined the room.",
                        BuildChatMessageKey(envelope));
                    break;

                case WhiteboardMessageType.UserLeft:
                    UserLeftPayload leftPayload = envelope.ReadPayload<UserLeftPayload>();
                    AppendChatLine(
                        $"[System] {leftPayload.UserName} left the room.",
                        BuildChatMessageKey(envelope));
                    break;

                case WhiteboardMessageType.StrokeStart:
                {
                    StrokeStartPayload payload = envelope.ReadPayload<StrokeStartPayload>();
                    if (!IsLocalStroke(payload.StrokeId))
                    {
                        DrawStrokeStart(payload);
                    }

                    break;
                }

                case WhiteboardMessageType.StrokePoint:
                {
                    StrokePointPayload payload = envelope.ReadPayload<StrokePointPayload>();
                    if (!IsLocalStroke(payload.StrokeId))
                    {
                        DrawStrokePoint(payload);
                    }

                    break;
                }

                case WhiteboardMessageType.StrokeEnd:
                {
                    StrokeEndPayload payload = envelope.ReadPayload<StrokeEndPayload>();
                    if (!IsLocalStroke(payload.StrokeId))
                    {
                        DrawStrokeEnd(payload);
                    }

                    break;
                }

                case WhiteboardMessageType.CanvasSnapshot:
                    RenderCanvasSnapshot(envelope.ReadPayload<CanvasSnapshotPayload>());
                    break;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Message handling error: {exception.Message}");
        }
    }

    private void DrawStrokeStart(StrokeStartPayload payload)
    {
        if (activeStrokes.ContainsKey(payload.StrokeId))
        {
            return;
        }

        LayerState layer = EnsureLayerExists(payload.LayerId);
        Polyline line = CreateStrokeLine(payload.UserName, payload.ColorHex, payload.Thickness);
        ApplyLayerToShape(line, layer.LayerId);
        line.Points.Add(NormalizeCanvasPoint(new Point(payload.X, payload.Y)));
        activeStrokes[payload.StrokeId] = line;
        strokeLayers[payload.StrokeId] = layer.LayerId;
        DrawingCanvas.Children.Add(line);
    }

    private void DrawStrokePoint(StrokePointPayload payload)
    {
        LayerState layer = EnsureLayerExists(payload.LayerId);

        if (!activeStrokes.TryGetValue(payload.StrokeId, out Polyline? line))
        {
            line = CreateStrokeLine(payload.UserName, payload.ColorHex, payload.Thickness);
            ApplyLayerToShape(line, layer.LayerId);
            activeStrokes[payload.StrokeId] = line;
            strokeLayers[payload.StrokeId] = layer.LayerId;
            DrawingCanvas.Children.Add(line);
        }
        else
        {
            line.Stroke = CreateStrokeBrush(payload.UserName, payload.ColorHex);
            line.StrokeThickness = NormalizeStrokeThickness(payload.Thickness);
            ApplyLayerToShape(line, layer.LayerId);
        }

        Point point = NormalizeCanvasPoint(new Point(payload.X, payload.Y));
        if (line.Points.Count > 0)
        {
            Point previousPoint = line.Points[line.Points.Count - 1];
            if (CalculateDistance(previousPoint, point) < MinRenderedStrokePointDistance)
            {
                return;
            }
        }

        line.Points.Add(point);
    }

    private void DrawStrokeEnd(StrokeEndPayload payload)
    {
        DrawStrokePoint(new StrokePointPayload(
            payload.StrokeId,
            payload.X,
            payload.Y,
            payload.UserName,
            payload.ColorHex,
            payload.Thickness,
            payload.LayerId));
        activeStrokes.Remove(payload.StrokeId);
        strokeLayers.Remove(payload.StrokeId);
    }

    private void RenderCanvasSnapshot(CanvasSnapshotPayload payload)
    {
        DrawingCanvas.Children.Clear();
        activeStrokes.Clear();
        strokeLayers.Clear();

        foreach (StrokeCommandPayload command in payload.Commands)
        {
            switch (command.EventType)
            {
                case "start":
                    DrawStrokeStart(new StrokeStartPayload(
                        command.StrokeId,
                        command.X,
                        command.Y,
                        command.UserName,
                        command.ColorHex,
                        command.Thickness,
                        command.LayerId));
                    break;

                case "point":
                    DrawStrokePoint(new StrokePointPayload(
                        command.StrokeId,
                        command.X,
                        command.Y,
                        command.UserName,
                        command.ColorHex,
                        command.Thickness,
                        command.LayerId));
                    break;

                case "end":
                    DrawStrokeEnd(new StrokeEndPayload(
                        command.StrokeId,
                        command.X,
                        command.Y,
                        command.UserName,
                        command.ColorHex,
                        command.Thickness,
                        command.LayerId));
                    break;
            }
        }

        RefreshAllLayerVisualStates();
    }

    private Polyline CreateStrokeLine(string userName, string colorHex, double thickness)
    {
        return new Polyline
        {
            Stroke = CreateStrokeBrush(userName, colorHex),
            StrokeThickness = NormalizeStrokeThickness(thickness),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
    }

    private static Brush CreateStrokeBrush(string userName, string colorHex)
    {
        return TryParseHexColor(colorHex, out Windows.UI.Color color)
            ? new SolidColorBrush(color)
            : CreateUserBrush(userName);
    }

    private static SolidColorBrush CreateUserBrush(string userName)
    {
        int hash = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(userName));

        byte red = (byte)(70 + (hash % 140));
        byte green = (byte)(90 + ((hash / 11) % 130));
        byte blue = (byte)(120 + ((hash / 29) % 110));

        return new SolidColorBrush(ColorHelper.FromArgb(255, red, green, blue));
    }
}
