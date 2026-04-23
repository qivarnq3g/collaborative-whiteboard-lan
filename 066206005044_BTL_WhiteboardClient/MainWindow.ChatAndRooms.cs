using System;
using System.Collections.Generic;
using System.Linq;
using Btl.Whiteboard.Client.Networking;
using Btl.Whiteboard.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private void BeginChatSession(string roomTitle, string endpoint)
    {
        ResetChatSession();
        AppendChatLine($"Connected to \"{roomTitle}\"");
    }

    private void ResetChatSession()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ResetChatSession);
            return;
        }

        chatMessages.Clear();
        chatMessageKeys.Clear();
        chatMessageKeyOrder.Clear();
        UpdateChatVisuals();
    }

    private static string BuildChatMessageKey(WhiteboardEnvelope envelope)
    {
        return $"{envelope.SenderId}:{envelope.Sequence}";
    }

    private bool TryReserveChatMessageKey(string? messageKey)
    {
        if (string.IsNullOrWhiteSpace(messageKey))
        {
            return true;
        }

        return chatMessageKeys.Add(messageKey);
    }

    private bool ShouldAutoScrollChat()
    {
        EnsureChatScrollViewer();
        if (chatListScrollViewer is null)
        {
            return true;
        }

        double distanceFromBottom = chatListScrollViewer.ScrollableHeight - chatListScrollViewer.VerticalOffset;
        return distanceFromBottom <= ChatAutoScrollThreshold;
    }

    private void ScrollChatToBottom()
    {
        if (ChatListView.Items.Count == 0)
        {
            return;
        }

        try
        {
            ChatListView.ScrollIntoView(ChatListView.Items[^1]);
        }
        catch
        {
        }
    }

    private void EnsureChatScrollViewer()
    {
        if (chatListScrollViewer is not null)
        {
            return;
        }

        chatListScrollViewer = FindDescendant<ScrollViewer>(ChatListView);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed)
        {
            return typed;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);
            T? match = FindDescendant<T>(child);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
        {
            return typed;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);
            T? match = FindDescendantByName<T>(child, name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void AppendChatLine(string line, string? messageKey = null)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => AppendChatLine(line, messageKey));
            return;
        }

        if (!TryReserveChatMessageKey(messageKey))
        {
            return;
        }

        bool shouldAutoScroll = ShouldAutoScrollChat();

        chatMessages.Add(line);
        chatMessageKeyOrder.Enqueue(messageKey);

        while (chatMessages.Count > MaxChatMessages)
        {
            chatMessages.RemoveAt(0);
            if (chatMessageKeyOrder.Count == 0)
            {
                continue;
            }

            string? evictedMessageKey = chatMessageKeyOrder.Dequeue();
            if (!string.IsNullOrWhiteSpace(evictedMessageKey))
            {
                chatMessageKeys.Remove(evictedMessageKey);
            }
        }

        if (shouldAutoScroll)
        {
            ScrollChatToBottom();
        }

        UpdateChatVisuals();
    }

    private void UpdateRoomVisuals()
    {
        RoomEmptyTextBlock.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ApplyDiscoveredRooms(IReadOnlyList<DiscoveredRoom> foundRooms)
    {
        string newSignature = BuildRoomSignature(foundRooms);
        if (string.Equals(newSignature, lastRoomsSignature, StringComparison.Ordinal))
        {
            return false;
        }

        string? selectedRoomKey = (RoomListView.SelectedItem as DiscoveredRoom) is DiscoveredRoom selectedRoom
            ? BuildRoomKey(selectedRoom)
            : null;

        rooms.Clear();
        foreach (DiscoveredRoom room in foundRooms)
        {
            rooms.Add(room);
        }

        lastRoomsSignature = newSignature;
        UpdateRoomVisuals();

        if (rooms.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(selectedRoomKey))
            {
                DiscoveredRoom? matchedRoom = rooms.FirstOrDefault(room =>
                    string.Equals(BuildRoomKey(room), selectedRoomKey, StringComparison.Ordinal));

                if (matchedRoom is not null)
                {
                    RoomListView.SelectedItem = matchedRoom;
                    return true;
                }
            }

            if (RoomListView.SelectedIndex < 0)
            {
                RoomListView.SelectedIndex = 0;
            }
        }

        return true;
    }

    private void UpdateOnlineUserVisuals()
    {
        int count = onlineUsers.Count;
        OnlineUsersCountTextBlock.Text = count.ToString();
        OnlineUsersEmptyTextBlock.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHostControlsVisibility();
    }

    private void UpdateChatVisuals()
    {
        ChatEmptyTextBlock.Visibility = chatMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetCompactState(string state)
    {
        if (state.StartsWith("Connection error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }

        if (string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase))
        {
            return "Connected";
        }

        if (string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase))
        {
            return "Connecting";
        }

        if (string.Equals(state, "Discovering", StringComparison.OrdinalIgnoreCase))
        {
            return "Discovering";
        }

        return "Disconnected";
    }

    private static string BuildDiscoveryStatusMessage(int roomCount)
    {
        return roomCount == 0
            ? "No rooms found. Check server status."
            : $"{roomCount} {(roomCount == 1 ? "room" : "rooms")} found";
    }

    private static string BuildRoomSignature(IEnumerable<DiscoveredRoom> discoveredRooms)
    {
        return string.Join(
            ';',
            discoveredRooms
                .Select(BuildRoomKey)
                .OrderBy(key => key, StringComparer.Ordinal));
    }

    private static string BuildRoomKey(DiscoveredRoom room)
    {
        return $"{room.ServerId}|{room.RoomName}|{room.Endpoint}";
    }
}
