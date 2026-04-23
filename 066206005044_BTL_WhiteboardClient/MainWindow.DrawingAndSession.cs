using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Btl.Whiteboard.Client.Networking;
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
    private void CanvasHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsAltPressedForZoom())
        {
            return;
        }

        e.Handled = true;

        int wheelDelta = e.GetCurrentPoint(CanvasHost).Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        double currentZoom = canvasZoom;
        double zoomFactor = Math.Pow(CanvasZoomStepMultiplier, wheelDelta / 120.0);
        double targetZoom = ClampCanvasZoom(currentZoom * zoomFactor);
        if (Math.Abs(targetZoom - currentZoom) < 0.0001)
        {
            return;
        }

        Point anchorOnHost = e.GetCurrentPoint(CanvasHost).Position;
        double worldX = (anchorOnHost.X - canvasPanX) / currentZoom;
        double worldY = (anchorOnHost.Y - canvasPanY) / currentZoom;

        canvasZoom = targetZoom;
        canvasPanX = anchorOnHost.X - (worldX * canvasZoom);
        canvasPanY = anchorOnHost.Y - (worldY * canvasZoom);

        ApplyCanvasViewportTransform();
        UpdateToolStatusText();
    }

    private async void CanvasHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var currentPointOnHost = e.GetCurrentPoint(CanvasHost);
        bool isMousePointer = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
        bool isLeftButtonPressed = currentPointOnHost.Properties.IsLeftButtonPressed;

        if (isMousePointer && IsSpacePressedForPan() && isLeftButtonPressed)
        {
            isSpacePanning = true;
            spacePanStartHostPoint = currentPointOnHost.Position;
            spacePanStartX = canvasPanX;
            spacePanStartY = canvasPanY;
            CanvasHost.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to a room to draw");
            return;
        }

        if (isMousePointer && !isLeftButtonPressed)
        {
            return;
        }

        Point point = GetCanvasPointFromHostPointer(e);
        UpdateCanvasPointerText(point);

        LayerState activeLayer = GetActiveLayerState();
        if (!activeLayer.IsVisible)
        {
            SetConnectionState("Error", $"Show \"{activeLayer.Name}\" layer to draw");
            return;
        }

        if (activeLayer.IsLocked)
        {
            SetConnectionState("Error", $"Unlock \"{activeLayer.Name}\" layer to draw");
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
        hasLastQueuedStrokePoint = true;
        lastQueuedStrokePoint = point;
        CanvasHost.CapturePointer(e.Pointer);
        strokeSendTimer.Stop();

        DrawStrokeStart(new StrokeStartPayload(
            activeStrokeId,
            point.X,
            point.Y,
            session.LocalSenderId,
            GetSafeUserName(),
            activeStrokeColorHex,
            activeStrokeThickness,
            activeStrokeLayerId));

        CancellationToken strokeSendToken = BeginActiveStrokeSendSession();

        activeStrokeStartSendTask = SafeSendStrokeAsync(() => session.SendStrokeStartAsync(
            activeStrokeId,
            point.X,
            point.Y,
            session.LocalSenderId,
            GetSafeUserName(),
            activeStrokeColorHex,
            activeStrokeThickness,
            activeStrokeLayerId,
            strokeSendToken));

        bool strokeStartSent = await EnsureActiveStrokeStartSentAsync();
        if (!strokeStartSent)
        {
            isDrawing = false;
            activeStrokeId = null;
            pendingStrokePoints.Clear();
            CancelActiveStrokeSendSession();
            TryReleasePointerCapture(e, "PointerPressed stroke start not sent");
            return;
        }

        if (isDrawing && !string.IsNullOrWhiteSpace(activeStrokeId))
        {
            strokeSendTimer.Start();
        }
    }

    private void CanvasHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (isSpacePanning)
        {
            Point hostPoint = e.GetCurrentPoint(CanvasHost).Position;
            canvasPanX = spacePanStartX + (hostPoint.X - spacePanStartHostPoint.X);
            canvasPanY = spacePanStartY + (hostPoint.Y - spacePanStartHostPoint.Y);
            ApplyCanvasViewportTransform();
            UpdateToolStatusText();
            e.Handled = true;
            return;
        }

        IReadOnlyList<Point> sampledPoints = GetPointerSamplePoints(e);
        Point point = sampledPoints[^1];
        UpdateCanvasPointerText(point);

        if (!session.IsConnected || !isDrawing)
        {
            return;
        }

        bool isMousePointer = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
        bool isLeftButtonPressed = e.GetCurrentPoint(CanvasHost).Properties.IsLeftButtonPressed;
        if (isMousePointer && !isLeftButtonPressed && !isStrokeFinalizing && !isWindowClosed)
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
        string userId = session.LocalSenderId;
        string userName = GetSafeUserName();

        foreach (Point sampledPoint in sampledPoints)
        {
            DrawStrokePoint(new StrokePointPayload(
                strokeId,
                sampledPoint.X,
                sampledPoint.Y,
                userId,
                userName,
                activeStrokeColorHex,
                activeStrokeThickness,
                activeStrokeLayerId));

            if (!ShouldQueueStrokePoint(sampledPoint))
            {
                continue;
            }

            if (pendingStrokePoints.Count >= MaxPendingStrokePoints)
            {
                pendingStrokePoints.Dequeue();
            }

            pendingStrokePoints.Enqueue(sampledPoint);
        }
    }

    private async void CanvasHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (isWindowClosed || isStrokeFinalizing)
        {
            return;
        }

        if (isSpacePanning)
        {
            isSpacePanning = false;
            TryReleasePointerCapture(e, "PointerReleased ending space pan");
            e.Handled = true;
            return;
        }

        if (!session.IsConnected || !isDrawing)
        {
            return;
        }

        Point point = GetCanvasPointFromHostPointer(e);
        UpdateCanvasPointerText(point);

        isStrokeFinalizing = true;
        Debug.WriteLine($"[WB-CLIENT][STROKE] PointerReleased entered. drawing={isDrawing}, activeStrokeId={(activeStrokeId ?? "<null>")}, pendingPoints={pendingStrokePoints.Count}");
        try
        {

            if (isShapePreviewActive)
            {
                isDrawing = false;
                isShapePreviewActive = false;
                TryReleasePointerCapture(e, "PointerReleased shape preview");
                RemoveShapePreview();

                IReadOnlyList<Point> shapePoints = BuildShapeStrokePoints(shapeStartPoint, point, selectedDrawingTool);
                await SendShapeStrokeAsync(shapePoints).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(activeStrokeId))
            {
                isDrawing = false;
                isShapePreviewActive = false;
                pendingStrokePoints.Clear();
                hasLastQueuedStrokePoint = false;
                activeStrokeStartSendTask = null;
                hasSentActiveStrokeStart = false;
                CancelActiveStrokeSendSession();
                strokeSendTimer.Stop();
                return;
            }

            string strokeId = activeStrokeId;
            string userId = session.LocalSenderId;
            string userName = GetSafeUserName();

            isDrawing = false;
            TryReleasePointerCapture(e, "PointerReleased finalize stroke");
            strokeSendTimer.Stop();

            bool strokeStartSent = await EnsureActiveStrokeStartSentAsync();
            if (!strokeStartSent)
            {
                Debug.WriteLine("[WB-CLIENT][STROKE] PointerReleased aborted: stroke start was not sent.");
                activeStrokeId = null;
                pendingStrokePoints.Clear();
                activeStrokeStartSendTask = null;
                hasSentActiveStrokeStart = false;
                CancelActiveStrokeSendSession();
                return;
            }

            DrawStrokePoint(new StrokePointPayload(
                strokeId,
                point.X,
                point.Y,
                session.LocalSenderId,
                userName,
                activeStrokeColorHex,
                activeStrokeThickness,
                activeStrokeLayerId));

            CancellationToken strokeSendToken = GetActiveStrokeSendToken();
            CancellationToken strokeEndSendToken = CancellationToken.None;
            if (ShouldQueueStrokePoint(point))
            {
                if (pendingStrokePoints.Count >= MaxPendingStrokePoints)
                {
                    pendingStrokePoints.Dequeue();
                }

                pendingStrokePoints.Enqueue(point);
            }

            bool drainCompleted = await DrainPendingStrokePointsBeforeEndAsync(strokeSendToken);
            if (!drainCompleted)
            {
                Debug.WriteLine($"[WB-CLIENT][STROKE] Drain incomplete before end. remainingPoints={pendingStrokePoints.Count}");
                pendingStrokePoints.Clear();
            }
            else
            {
                Debug.WriteLine("[WB-CLIENT][STROKE] Drain completed before stroke end send.");
            }

            activeStrokeId = null;
            pendingStrokePoints.Clear();
            activeStrokeStartSendTask = null;
            hasSentActiveStrokeStart = false;
            hasLastQueuedStrokePoint = false;

            await SafeSendStrokeAsync(() => session.SendStrokeEndAsync(
                strokeId,
                point.X,
                point.Y,
                userId,
                userName,
                activeStrokeColorHex,
                activeStrokeThickness,
                activeStrokeLayerId,
                strokeEndSendToken));

            Debug.WriteLine($"[WB-CLIENT][STROKE] StrokeEnd send attempted. strokeId={strokeId}");

            CancelActiveStrokeSendSession();

            activeStrokes.Remove(strokeId);
            strokeLayers.Remove(strokeId);

            activeStrokeColorHex = GetEffectiveStrokeColorHex();
            activeStrokeThickness = GetEffectiveStrokeThickness();
            activeStrokeLayerId = activeLayerId;
        }
        catch (OperationCanceledException) when (isWindowClosed)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[WB-CLIENT][STROKE] PointerReleased exception: {exception}");
            if (!isWindowClosed)
            {
                SetConnectionState("Error", $"Stroke finalize failed: {exception.Message}");
            }

            ResetLocalDrawingState();
        }
        finally
        {
            isStrokeFinalizing = false;
            Debug.WriteLine($"[WB-CLIENT][STROKE] PointerReleased exit. drawing={isDrawing}, finalizing={isStrokeFinalizing}, pendingPoints={pendingStrokePoints.Count}, activeStrokeId={(activeStrokeId ?? "<null>")}");
        }
    }

    private void CanvasHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!isDrawing && !isShapePreviewActive && !isSpacePanning)
        {
            return;
        }

        isSpacePanning = false;
        ResetLocalDrawingState();
    }

    private void CanvasHost_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        isSpacePanning = false;
        TryReleasePointerCapture(e, "PointerCanceled");
        ResetLocalDrawingState();
    }

    private void TryReleasePointerCapture(PointerRoutedEventArgs e, string context)
    {
        if (isWindowClosed)
        {
            return;
        }

        try
        {
            CanvasHost.ReleasePointerCapture(e.Pointer);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[WB-CLIENT][POINTER] ReleasePointerCapture failed ({context}): {exception.Message}");
        }
    }

    private Point NormalizeCanvasPoint(Point point)
    {
        double maxX = Math.Max(0, DrawingCanvas.ActualWidth - 1);
        double maxY = Math.Max(0, DrawingCanvas.ActualHeight - 1);
        return new Point(
            Math.Clamp(point.X, 0, maxX),
            Math.Clamp(point.Y, 0, maxY));
    }

    private Point GetCanvasPointFromHostPointer(PointerRoutedEventArgs e)
    {
        Point hostPoint = e.GetCurrentPoint(CanvasHost).Position;
        return NormalizeCanvasPoint(ConvertHostPointToCanvasPoint(hostPoint));
    }

    private Point ConvertHostPointToCanvasPoint(Point hostPoint)
    {
        double zoom = canvasZoom <= 0 ? DefaultCanvasZoom : canvasZoom;
        double canvasX = (hostPoint.X - canvasPanX) / zoom;
        double canvasY = (hostPoint.Y - canvasPanY) / zoom;
        return new Point(canvasX, canvasY);
    }

    private IReadOnlyList<Point> GetPointerSamplePoints(PointerRoutedEventArgs e)
    {
        var intermediatePoints = e.GetIntermediatePoints(CanvasHost);
        if (intermediatePoints.Count == 0)
        {
            return new[] { GetCanvasPointFromHostPointer(e) };
        }

        bool isDescendingByTimestamp = intermediatePoints.Count > 1 &&
            intermediatePoints[0].Timestamp > intermediatePoints[intermediatePoints.Count - 1].Timestamp;

        List<Point> points = new(intermediatePoints.Count);

        if (isDescendingByTimestamp)
        {
            for (int index = intermediatePoints.Count - 1; index >= 0; index--)
            {
                points.Add(NormalizeCanvasPoint(ConvertHostPointToCanvasPoint(intermediatePoints[index].Position)));
            }
        }
        else
        {
            for (int index = 0; index < intermediatePoints.Count; index++)
            {
                points.Add(NormalizeCanvasPoint(ConvertHostPointToCanvasPoint(intermediatePoints[index].Position)));
            }
        }

        return points;
    }

    private async void StrokeSendTimer_Tick(object? sender, object e)
    {
        try
        {
            await SendPendingStrokePointAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Stroke loop failed: {exception.Message}");
        }
    }

    private async Task SendPendingStrokePointAsync()
    {
        if (!await strokeSendLoopGate.WaitAsync(0))
        {
            return;
        }

        if (!session.IsConnected ||
            string.IsNullOrWhiteSpace(activeStrokeId) ||
            pendingStrokePoints.Count == 0)
        {
            strokeSendLoopGate.Release();
            return;
        }

        CancellationToken strokeSendToken = GetActiveStrokeSendToken();
        if (strokeSendToken.IsCancellationRequested)
        {
            strokeSendLoopGate.Release();
            return;
        }

        if (!hasSentActiveStrokeStart)
        {
            try
            {
                bool strokeStartSent = await EnsureActiveStrokeStartSentAsync();
                if (!strokeStartSent)
                {
                    pendingStrokePoints.Clear();
                    strokeSendLoopGate.Release();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                strokeSendLoopGate.Release();
                return;
            }
        }

        string strokeId = activeStrokeId;
        string userId = session.LocalSenderId;
        string userName = GetSafeUserName();
        int sendLimit = MaxStrokePointsPerFlush;
        int sentCount = 0;

        try
        {
            while (session.IsConnected &&
                   !strokeSendToken.IsCancellationRequested &&
                   pendingStrokePoints.Count > 0 &&
                   sentCount < sendLimit)
            {
                Point point = pendingStrokePoints.Dequeue();
                if (hasLastSentStrokePoint &&
                    CalculateDistance(lastSentStrokePoint, point) < MinStrokePointDistance)
                {
                    continue;
                }

                await session.SendStrokePointAsync(
                    strokeId,
                    point.X,
                    point.Y,
                    userId,
                    userName,
                    activeStrokeColorHex,
                    activeStrokeThickness,
                    activeStrokeLayerId,
                    strokeSendToken).ConfigureAwait(false);

                lastSentStrokePoint = point;
                hasLastSentStrokePoint = true;
                sentCount++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Stroke send failed: {exception.Message}");
            CancelActiveStrokeSendSession();
        }
        finally
        {
            strokeSendLoopGate.Release();
        }
    }

    private bool ShouldQueueStrokePoint(Point point)
    {
        if (!hasLastQueuedStrokePoint)
        {
            lastQueuedStrokePoint = point;
            hasLastQueuedStrokePoint = true;
            return true;
        }

        double requiredDistance = MinStrokePointDistance;
        if (pendingStrokePoints.Count > MaxPendingStrokePoints / 2)
        {
            requiredDistance *= 1.75;
        }

        if (pendingStrokePoints.Count > (MaxPendingStrokePoints * 3) / 4)
        {
            requiredDistance *= 1.5;
        }

        if (CalculateDistance(lastQueuedStrokePoint, point) < requiredDistance)
        {
            return false;
        }

        lastQueuedStrokePoint = point;
        return true;
    }

    private async Task<bool> DrainPendingStrokePointsBeforeEndAsync(CancellationToken strokeSendToken)
    {
        int iterations = 0;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + MaxReleaseDrainDuration;

        while (session.IsConnected &&
               !strokeSendToken.IsCancellationRequested &&
               pendingStrokePoints.Count > 0)
        {
            if (iterations >= MaxReleaseDrainIterations || DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            int queuedBeforeFlush = pendingStrokePoints.Count;
            await SendPendingStrokePointAsync();
            if (pendingStrokePoints.Count == 0)
            {
                return true;
            }

            if (pendingStrokePoints.Count >= queuedBeforeFlush)
            {
                return false;
            }

            iterations++;

            try
            {
                await Task.Delay(StrokePointInterval, strokeSendToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return pendingStrokePoints.Count == 0;
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

        bool sent = await strokeStartTask;
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
            await sendAction();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
                    if (chatMessages.Count == 0)
                    {
                        string roomTitle = (RoomListView.SelectedItem as DiscoveredRoom)?.Title ?? "Selected room";
                        BeginChatSession(roomTitle, connectedEndpoint);
                    }

                    return;
                }

                if (state.StartsWith("Connection error", StringComparison.OrdinalIgnoreCase))
                {
                    ResetLocalDrawingState();
                    SetConnectionState("Error", state);
                    return;
                }

                if (state.StartsWith("Kicked:", StringComparison.OrdinalIgnoreCase) ||
                    state.StartsWith("Banned:", StringComparison.OrdinalIgnoreCase) ||
                    state.StartsWith("Room locked:", StringComparison.OrdinalIgnoreCase))
                {
                    ResetLocalDrawingState();
                    ResetOwnershipState();
                    SetConnectionState("Disconnected", state);
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
                    bool currentUserIsOwner = string.Equals(snapshotPayload.RoomOwnerId, session.LocalSenderId, StringComparison.Ordinal);
                    foreach (PresenceUserPayload user in snapshotPayload.Users)
                    {
                        knownUserNames[user.UserId] = user.UserName;
                        bool isOwner = string.Equals(user.UserId, snapshotPayload.RoomOwnerId, StringComparison.Ordinal);
                        onlineUsers.Add(new OnlineUserItem
                        {
                            UserId = user.UserId,
                            UserName = user.UserName,
                            IsOwner = isOwner,
                            CanModerate = currentUserIsOwner &&
                                          !string.Equals(user.UserId, session.LocalSenderId, StringComparison.Ordinal) &&
                                          !isOwner
                        });
                    }

                    UpdateOwnershipDisplay(snapshotPayload.RoomOwnerId, null);
                    UpdateOnlineUserVisuals();
                    break;

                case WhiteboardMessageType.UserJoined:
                    UserJoinedPayload joinedPayload = envelope.ReadPayload<UserJoinedPayload>();
                    AppendChatLine(
                        $"[System] {joinedPayload.UserName} joined the room.",
                        BuildChatMessageKey(envelope));
                    knownUserNames[joinedPayload.UserId] = joinedPayload.UserName;
                    bool isJoinedOwner = string.Equals(joinedPayload.UserId, roomOwnerId, StringComparison.Ordinal);
                    onlineUsers.Add(new OnlineUserItem
                    {
                        UserId = joinedPayload.UserId,
                        UserName = joinedPayload.UserName,
                        IsOwner = isJoinedOwner,
                        CanModerate = IsCurrentUserOwner() &&
                                      !string.Equals(joinedPayload.UserId, session.LocalSenderId, StringComparison.Ordinal) &&
                                      !isJoinedOwner
                    });
                    UpdateOnlineUserVisuals();
                    break;

                case WhiteboardMessageType.UserLeft:
                    UserLeftPayload leftPayload = envelope.ReadPayload<UserLeftPayload>();
                    AppendChatLine(
                        $"[System] {leftPayload.UserName} left the room.",
                        BuildChatMessageKey(envelope));
                    OnlineUserItem? toRemove = null;
                    foreach (OnlineUserItem item in onlineUsers)
                    {
                        if (string.Equals(item.UserId, leftPayload.UserId, StringComparison.Ordinal))
                        {
                            toRemove = item;
                            break;
                        }
                    }
                    if (toRemove is not null)
                    {
                        onlineUsers.Remove(toRemove);
                    }

                    UpdateOnlineUserVisuals();
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

                case WhiteboardMessageType.RoomStateInfo:
                    RoomStateInfoPayload roomStatePayload = envelope.ReadPayload<RoomStateInfoPayload>();
                    isRoomLocked = roomStatePayload.IsRoomLocked;
                    ApplyAuthoritativeRoomState(roomStatePayload);
                    UpdateOwnershipDisplay(roomStatePayload.RoomOwnerId, roomStatePayload.RoomOwnerUserName);
                    UpdateRoomLockVisuals();
                    UpdatePasswordInputGuidance();
                    break;

                case WhiteboardMessageType.OwnerAssigned:
                    OwnerAssignedPayload ownerPayload = envelope.ReadPayload<OwnerAssignedPayload>();
                    UpdateOwnershipDisplay(ownerPayload.OwnerId, ownerPayload.OwnerUserName);
                    break;

                case WhiteboardMessageType.RoomLocked:
                    RoomLockedPayload roomLockedPayload = envelope.ReadPayload<RoomLockedPayload>();
                    isRoomLocked = true;
                    UpdateRoomLockVisuals();
                    SetStatus(string.IsNullOrWhiteSpace(roomLockedPayload.Reason)
                        ? "Room is now locked by owner."
                        : $"Room locked: {roomLockedPayload.Reason}");
                    break;

                case WhiteboardMessageType.UnlockRoom:
                    isRoomLocked = false;
                    UpdateRoomLockVisuals();
                    SetStatus("Room unlocked by owner.");
                    break;

                case WhiteboardMessageType.BanListSnapshot:
                    BanListPayload banListPayload = envelope.ReadPayload<BanListPayload>();
                    ApplyBanListSnapshot(banListPayload.UserIds);
                    break;

                case WhiteboardMessageType.UserRenamed:
                    UserRenamedPayload renamedPayload = envelope.ReadPayload<UserRenamedPayload>();
                    knownUserNames[renamedPayload.UserId] = renamedPayload.NewUserName;
                    foreach (OnlineUserItem userItem in onlineUsers)
                    {
                        if (string.Equals(userItem.UserId, renamedPayload.UserId, StringComparison.Ordinal))
                        {
                            int idx = onlineUsers.IndexOf(userItem);
                            onlineUsers[idx] = new OnlineUserItem
                            {
                                UserId = userItem.UserId,
                                UserName = renamedPayload.NewUserName,
                                IsOwner = userItem.IsOwner,
                                CanModerate = userItem.CanModerate
                            };
                            break;
                        }
                    }
                    UpdateOnlineUserVisuals();
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
            payload.UserId,
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
                        command.UserId,
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
                        command.UserId,
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
                        command.UserId,
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
