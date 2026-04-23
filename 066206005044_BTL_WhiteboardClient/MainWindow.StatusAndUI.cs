using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        await SendUndoAsync();
    }

    private async void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        await SendRedoAsync();
    }

    private async Task SendUndoAsync()
    {
        Debug.WriteLine($"[WB-CLIENT][UNDO] Request started. connected={session.IsConnected}, drawing={isDrawing}, shapePreview={isShapePreviewActive}, finalizing={isStrokeFinalizing}, pendingPoints={pendingStrokePoints.Count}, activeStrokeId={(activeStrokeId ?? "<null>")}");

        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to a room before undo.");
            Debug.WriteLine("[WB-CLIENT][UNDO] Blocked: session not connected.");
            return;
        }

        if (!CanApplyHistoryAction("Undo"))
        {
            Debug.WriteLine("[WB-CLIENT][UNDO] Blocked by CanApplyHistoryAction.");
            return;
        }

        try
        {
            await session.SendUndoAsync(GetSafeUserName(), CancellationToken.None).ConfigureAwait(false);
            Debug.WriteLine("[WB-CLIENT][UNDO] Request sent to server.");
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Undo failed: {exception.Message}");
            Debug.WriteLine($"[WB-CLIENT][UNDO] Send failed: {exception.Message}");
        }
    }

    private async Task SendRedoAsync()
    {
        Debug.WriteLine($"[WB-CLIENT][REDO] Request started. connected={session.IsConnected}, drawing={isDrawing}, shapePreview={isShapePreviewActive}, finalizing={isStrokeFinalizing}, pendingPoints={pendingStrokePoints.Count}, activeStrokeId={(activeStrokeId ?? "<null>")}");

        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to a room before redo.");
            Debug.WriteLine("[WB-CLIENT][REDO] Blocked: session not connected.");
            return;
        }

        if (!CanApplyHistoryAction("Redo"))
        {
            Debug.WriteLine("[WB-CLIENT][REDO] Blocked by CanApplyHistoryAction.");
            return;
        }

        try
        {
            await session.SendRedoAsync(GetSafeUserName(), CancellationToken.None).ConfigureAwait(false);
            Debug.WriteLine("[WB-CLIENT][REDO] Request sent to server.");
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", "Redo error");
            Debug.WriteLine($"[WB-CLIENT][REDO] Send failed: {exception.Message}");
        }
    }

    private bool CanApplyHistoryAction(string actionName)
    {
        bool hasStaleDrawingState = !isDrawing &&
            !isShapePreviewActive &&
            (activeStrokeId is not null || pendingStrokePoints.Count > 0 || hasSentActiveStrokeStart);
        if (hasStaleDrawingState)
        {
            ResetLocalDrawingState();
        }

        bool strokeStartPending = activeStrokeStartSendTask is { IsCompleted: false };
        if (isDrawing || isShapePreviewActive || isStrokeFinalizing || strokeStartPending || pendingStrokePoints.Count > 0)
        {
            Debug.WriteLine($"[WB-CLIENT][{actionName.ToUpperInvariant()}] Blocked. drawing={isDrawing}, shapePreview={isShapePreviewActive}, finalizing={isStrokeFinalizing}, strokeStartPending={strokeStartPending}, pendingPoints={pendingStrokePoints.Count}, activeStrokeId={(activeStrokeId ?? "<null>")}");

            if (isStrokeFinalizing)
            {
                SetStatus($"{actionName} busy. Retry in a moment.");
            }
            else
            {
                SetStatus($"{actionName} busy. Release pointer.");
            }

            return false;
        }

        Debug.WriteLine($"[WB-CLIENT][{actionName.ToUpperInvariant()}] Allowed.");
        return true;
    }

    private void SetStatus(string status)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ConnectionStatusTextBlock.Text = status;
            return;
        }

        DispatcherQueue.TryEnqueue(() => ConnectionStatusTextBlock.Text = status);
    }

    private void SetConnectionState(string state, string detail)
    {
        if (string.Equals(connectionState, state, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(string.IsNullOrWhiteSpace(detail) ? state : $"{state}: {detail}");
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateConnectionControls(state);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => UpdateConnectionControls(state));
            }

            return;
        }

        connectionState = state;

        string statusText = string.IsNullOrWhiteSpace(detail)
            ? state
            : $"{state}: {detail}";

        SetStatus(statusText);

        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateConnectionControls(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionControls(state);
        });
    }

    private void UpdateConnectionControls(string state)
    {
        ConnectButton.Content = session.IsConnected ? "Disconnect" : "Connect";
        ConnectButton.IsEnabled = !isConnectInProgress &&
            !string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase);
        CanvasHintTextBlock.Visibility = session.IsConnected ? Visibility.Collapsed : Visibility.Visible;

        ConnectionBadgeTextBlock.Text = state;
        CanvasConnectionTextBlock.Text = state;

        ApplyStatusBadge(state);

        if (!session.IsConnected)
        {
            CanvasHintTextBlock.Text = string.Equals(state, "Discovering", StringComparison.OrdinalIgnoreCase)
                ? "Scanning for rooms..."
                : "Connect to start drawing";
        }

        UpdateBannedUsersVisuals();
        UpdateRoomLockVisuals();
        UpdatePasswordInputGuidance();
    }

    private void ApplyStatusBadge(string state)
    {
        string foregroundKey;
        string backgroundKey;

        if (state.StartsWith("Connection error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
        {
            foregroundKey = "ErrorBadgeTextBrush";
            backgroundKey = "ErrorBadgeBackgroundBrush";
        }
        else if (string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase))
        {
            foregroundKey = "PositiveBadgeTextBrush";
            backgroundKey = "PositiveBadgeBackgroundBrush";
        }
        else if (string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(state, "Discovering", StringComparison.OrdinalIgnoreCase))
        {
            foregroundKey = "WarningBadgeTextBrush";
            backgroundKey = "WarningBadgeBackgroundBrush";
        }
        else
        {
            foregroundKey = "BadgeTextBrush";
            backgroundKey = "BadgeBackgroundBrush";
        }

        Brush foregroundBrush = ResolveThemeBrush(foregroundKey);
        Brush backgroundBrush = ResolveThemeBrush(backgroundKey);

        ConnectionBadgeBorder.Background = backgroundBrush;
        CanvasConnectionBadgeBorder.Background = backgroundBrush;

        ConnectionBadgeBorder.BorderBrush = foregroundBrush;
        CanvasConnectionBadgeBorder.BorderBrush = foregroundBrush;

        ConnectionStateDot.Fill = foregroundBrush;
        CanvasConnectionStateDot.Fill = foregroundBrush;

        ConnectionBadgeTextBlock.Foreground = foregroundBrush;
        CanvasConnectionTextBlock.Foreground = foregroundBrush;
    }

    private void UpdateOwnershipDisplay(string? ownerId, string? ownerUserName)
    {
        roomOwnerId = ownerId;
        if (ownerUserName is not null)
        {
            roomOwnerUserName = ownerUserName;
        }

        UpdateHostControlsVisibility();
        UpdateOnlineUserListItemActions();
    }

    private bool IsCurrentUserOwner()
    {
        return string.Equals(roomOwnerId, session.LocalSenderId, StringComparison.Ordinal);
    }

    private void UpdateHostControlsVisibility()
    {
        bool isOwner = IsCurrentUserOwner();
        if (HostControlsPanel is not null)
        {
            HostControlsPanel.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!isOwner)
        {
            bannedUsers.Clear();
        }

        UpdateBannedUsersVisuals();
        UpdateRoomLockVisuals();
    }

    private void UpdateRoomLockVisuals()
    {
        if (HostLockRoomButton is not null)
        {
            HostLockRoomButton.Content = isRoomLocked ? "Unlock" : "Lock";
        }

        if (HostRoomAccessTextBlock is not null)
        {
            HostRoomAccessTextBlock.Text = isRoomLocked
                ? "Locked"
                : "Open";
        }
    }
}