using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Btl.Whiteboard.Client.Networking;
using Btl.Whiteboard.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private async void RefreshRoomsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRoomsAsync(DiscoveryRefreshOrigin.Manual);
    }

    private async Task RefreshRoomsAsync(DiscoveryRefreshOrigin origin)
    {
        if (isWindowClosed)
        {
            return;
        }

        if (Interlocked.Exchange(ref refreshRoomsInProgress, 1) == 1)
        {
            return;
        }

        bool isBackgroundRefresh = origin == DiscoveryRefreshOrigin.Background;
        bool shouldDisableRefreshButton = origin == DiscoveryRefreshOrigin.Manual;
        CancellationToken refreshToken = refreshRoomsCancellationSource.Token;

        try
        {
            if (shouldDisableRefreshButton)
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    RefreshRoomsButton.IsEnabled = false;
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!isWindowClosed)
                        {
                            RefreshRoomsButton.IsEnabled = false;
                        }
                    });
                }
            }

            bool shouldAnnounceScanning = !isBackgroundRefresh && !session.IsConnected;
            if (shouldAnnounceScanning)
            {
                SetConnectionState("Discovering", "Scanning for rooms...");
            }

            IReadOnlyList<DiscoveredRoom> foundRooms = await discoveryClient
                .DiscoverAsync(DiscoveryProtocol.DefaultDiscoveryPort, string.Empty, refreshToken)
                .ConfigureAwait(false);

            if (refreshToken.IsCancellationRequested || isWindowClosed)
            {
                return;
            }

            IReadOnlyList<DiscoveredRoom> safeRooms = foundRooms ?? Array.Empty<DiscoveredRoom>();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (isWindowClosed)
                {
                    return;
                }

                bool roomsChanged = ApplyDiscoveredRooms(safeRooms);

                if (!session.IsConnected && (shouldAnnounceScanning || (isBackgroundRefresh && roomsChanged)))
                {
                    SetConnectionState("Disconnected", BuildDiscoveryStatusMessage(rooms.Count));
                }
            });
        }
        catch (OperationCanceledException) when (refreshRoomsCancellationSource.IsCancellationRequested || isWindowClosed)
        {
        }
        catch (Exception exception)
        {
            if (!isBackgroundRefresh && !isWindowClosed)
            {
                SetConnectionState("Error", $"Discovery error: {exception.Message}");
            }
        }
        finally
        {
            if (shouldDisableRefreshButton && !isWindowClosed)
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    RefreshRoomsButton.IsEnabled = true;
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!isWindowClosed)
                        {
                            RefreshRoomsButton.IsEnabled = true;
                        }
                    });
                }
            }

            Interlocked.Exchange(ref refreshRoomsInProgress, 0);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (session.IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (isConnectInProgress)
            return;

        isConnectInProgress = true;
        ConnectButton.IsEnabled = false;

        try
        {
            await ConnectToSelectedRoomAsync();
        }
        finally
        {
            isConnectInProgress = false;
            if (string.Equals(connectionState, "Connecting", StringComparison.OrdinalIgnoreCase))
            {
                connectionState = "Disconnected";
            }
            UpdateConnectionControls(connectionState);
        }
    }

    private async Task ConnectToSelectedRoomAsync()
    {
        SetStatus(string.Empty);

        if (RoomListView.SelectedItem is not DiscoveredRoom room)
        {
            SetStatus("Select a room to connect");
            return;
        }

        if (room.ProtocolVersion is not int protocolVersion ||
            protocolVersion != DiscoveryProtocol.CurrentProtocolVersion)
        {
            string roomProtocolText = room.ProtocolVersion?.ToString() ?? "unknown";
            SetConnectionState(
                "Error",
                $"Incompatible room protocol ({roomProtocolText}). Expected protocol {DiscoveryProtocol.CurrentProtocolVersion}. Rebuild/restart the server.");
            return;
        }

        if (room.IsPasswordProtected && string.IsNullOrWhiteSpace(RoomPasswordBox.Password))
        {
            SetConnectionState("Error", "Room requires password.");
            return;
        }

        bool isFirstJoinMode = IsFirstJoinPasswordSetupMode(room);

        if (isFirstJoinMode)
        {
            DiscoveredRoom? freshRoom = await discoveryClient.PingServerAsync(
                room.Address, DiscoveryProtocol.DefaultDiscoveryPort, CancellationToken.None);
            if (freshRoom is not null && string.Equals(freshRoom.ServerId, room.ServerId, StringComparison.Ordinal))
            {
                room = freshRoom;
                isFirstJoinMode = IsFirstJoinPasswordSetupMode(room);
                if (!isFirstJoinMode)
                {
                    isRoomPasswordPrepared = false;
                    preparedRoomPassword = null;
                    preparedRoomKey = null;
                    UpdatePasswordInputGuidance();
                }
            }
        }

        if (room.IsPasswordProtected && string.IsNullOrWhiteSpace(RoomPasswordBox.Password))
        {
            SetConnectionState("Error", "Room requires password.");
            return;
        }

        string selectedRoomKey = BuildRoomKey(room);
        if (isFirstJoinMode &&
            !string.IsNullOrWhiteSpace(RoomPasswordBox.Password) &&
            (!isRoomPasswordPrepared ||
             !string.Equals(preparedRoomKey, selectedRoomKey, StringComparison.Ordinal) ||
             !string.Equals(preparedRoomPassword, RoomPasswordBox.Password, StringComparison.Ordinal)))
        {
            SetConnectionState("Error", "Set a password first, then connect");
            return;
        }

        string userName = GetSafeUserName();
        string? password = isFirstJoinMode
            ? (isRoomPasswordPrepared ? preparedRoomPassword : null)
            : (string.IsNullOrWhiteSpace(RoomPasswordBox.Password) ? null : RoomPasswordBox.Password);

        try
        {
            SetConnectionState("Connecting", "Connecting...");
            await session.ConnectAsync(room.Address, room.TcpPort, userName, password, CancellationToken.None);
            connectedEndpoint = room.Endpoint;
        }
        catch (Exception exception)
        {
            string message = exception.Message;

            bool isBanned = message.StartsWith("Banned:", StringComparison.OrdinalIgnoreCase);
            bool isRoomLockedMsg = message.StartsWith("Room locked:", StringComparison.OrdinalIgnoreCase);
            bool isAuthFailed = message.StartsWith("Authentication failed:", StringComparison.OrdinalIgnoreCase);

            if (isAuthFailed && isFirstJoinMode)
            {
                _ = await HandleFirstUserRaceAsync(room, userName);
                return;
            }

            if (isBanned || isRoomLockedMsg || isAuthFailed)
            {
                SetConnectionState("Error", message);
                return;
            }

            SetConnectionState("Error", $"Connect failed: {message}");
        }
    }

    private async Task<bool> HandleFirstUserRaceAsync(DiscoveredRoom racedRoom, string userName)
    {
        DiscoveredRoom? freshRoom = await discoveryClient.PingServerAsync(
            racedRoom.Address, DiscoveryProtocol.DefaultDiscoveryPort, CancellationToken.None);

        if (freshRoom is not null && string.Equals(freshRoom.ServerId, racedRoom.ServerId, StringComparison.Ordinal))
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (string.Equals(rooms[i].ServerId, freshRoom.ServerId, StringComparison.Ordinal))
                {
                    rooms[i] = freshRoom;
                    RoomListView.SelectedItem = freshRoom;
                    break;
                }
            }
        }

        isRoomPasswordPrepared = false;
        preparedRoomPassword = null;
        preparedRoomKey = null;
        UpdatePasswordInputGuidance();

        if (freshRoom is not null && !freshRoom.IsFirstUserMode && !freshRoom.IsPasswordProtected)
        {
            try
            {
                RoomPasswordBox.Password = string.Empty;
                SetConnectionState("Connecting", "Joining...");
                await session.ConnectAsync(freshRoom.Address, freshRoom.TcpPort, userName, null, CancellationToken.None);
                connectedEndpoint = freshRoom.Endpoint;
                SetStatus("Joined.");
                return true;
            }
            catch (Exception retryException)
            {
                SetConnectionState("Error", $"Retry after race failed: {retryException.Message}");
                return false;
            }
        }

        SetConnectionState("Error", "Another client connected first and set the room password. Enter the password and try again.");
        return false;
    }

    private async Task DisconnectAsync()
    {
        await session.DisconnectAsync();
        ResetOwnershipState();
        SetConnectionState("Disconnected", "Disconnected by user.");
        AppendChatLine("[System] Disconnected.");
    }

    private void ResetOwnershipState()
    {
        roomOwnerId = null;
        roomOwnerUserName = null;
        isRoomLocked = false;
        isRoomPasswordPrepared = false;
        preparedRoomPassword = null;
        preparedRoomKey = null;
        selectedTargetUserId = null;
        bannedUsers.Clear();
        UpdateHostControlsVisibility();
        UpdateOnlineUserListItemActions();
        UpdatePasswordInputGuidance();
    }

    private async void SendChatButton_Click(object sender, RoutedEventArgs e)
    {
        await SendChatAsync();
    }

    private async void ChatInputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to chat");
            return;
        }

        string text = ChatInputTextBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        ChatInputTextBox.Text = string.Empty;

        try
        {
            await session.SendChatAsync(GetSafeUserName(), text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Send chat failed: {exception.Message}");
        }
    }

    private string GetSafeUserName()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            if (!string.IsNullOrWhiteSpace(lastKnownUserName))
            {
                return lastKnownUserName;
            }

            return $"User-{Environment.MachineName}";
        }

        string userName = UserNameTextBox.Text.Trim();
        if (userName.Length == 0)
        {
            userName = $"User-{Environment.MachineName}";
            UserNameTextBox.Text = userName;
        }

        lastKnownUserName = userName;
        return userName;
    }

    private void UpdatePasswordInputGuidance()
    {
        if (SaveRoomPasswordButton is null)
        {
            return;
        }

        if (RoomListView.SelectedItem is not DiscoveredRoom room)
        {
            SaveRoomPasswordButton.Visibility = Visibility.Collapsed;
            RoomPasswordBox.PlaceholderText = "Optional (required if locked)";
            return;
        }

        bool isFirstJoinMode = IsFirstJoinPasswordSetupMode(room);
        SaveRoomPasswordButton.Visibility = isFirstJoinMode ? Visibility.Visible : Visibility.Collapsed;

        if (room.IsPasswordProtected)
        {
            RoomPasswordBox.PlaceholderText = "Enter room password";
            return;
        }

        if (isFirstJoinMode)
        {
            RoomPasswordBox.PlaceholderText = "Create password (optional)";
            return;
        }

        RoomPasswordBox.PlaceholderText = "No password required";
    }

    private static bool IsFirstJoinPasswordSetupMode(DiscoveredRoom room)
    {
        return room.IsFirstUserMode;
    }

    private void ApplyAuthoritativeRoomState(RoomStateInfoPayload roomState)
    {
        if (RoomListView.SelectedItem is not DiscoveredRoom selectedRoom)
        {
            return;
        }

        DiscoveredRoom updatedRoom = new()
        {
            ServerId = selectedRoom.ServerId,
            ServerName = selectedRoom.ServerName,
            RoomName = selectedRoom.RoomName,
            Address = selectedRoom.Address,
            TcpPort = selectedRoom.TcpPort,
            ProtocolVersion = selectedRoom.ProtocolVersion,
            IsPasswordProtected = roomState.IsPasswordProtected,
            OwnerUserName = roomState.RoomOwnerUserName,
            IsFirstUserMode = roomState.IsFirstUserMode
        };

        for (int i = 0; i < rooms.Count; i++)
        {
            if (!string.Equals(rooms[i].ServerId, selectedRoom.ServerId, StringComparison.Ordinal))
            {
                continue;
            }

            rooms[i] = updatedRoom;
            RoomListView.SelectedItem = updatedRoom;
            break;
        }

        if (!roomState.IsFirstUserMode)
        {
            isRoomPasswordPrepared = false;
            preparedRoomPassword = null;
            preparedRoomKey = null;
        }
    }

    private async void SaveRoomPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (RoomListView.SelectedItem is not DiscoveredRoom room || !IsFirstJoinPasswordSetupMode(room))
        {
            SetStatus("Room password can only be created in first-user mode.");
            return;
        }

        if (string.IsNullOrWhiteSpace(RoomPasswordBox.Password))
        {
            isRoomPasswordPrepared = false;
            preparedRoomPassword = null;
            preparedRoomKey = null;
            SetStatus("Password cleared. Room will stay open when you connect first.");
            UpdatePasswordInputGuidance();
            SaveRoomPasswordButton.Content = "Cleared";
            await Task.Delay(1500);
            SaveRoomPasswordButton.Content = "Save Password";
            return;
        }

        DiscoveredRoom? freshRoom = await discoveryClient.PingServerAsync(
            room.Address,
            DiscoveryProtocol.DefaultDiscoveryPort,
            CancellationToken.None);

        if (freshRoom is not null && string.Equals(freshRoom.ServerId, room.ServerId, StringComparison.Ordinal))
        {
            room = freshRoom;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (!string.Equals(rooms[i].ServerId, freshRoom.ServerId, StringComparison.Ordinal))
                {
                    continue;
                }

                rooms[i] = freshRoom;
                RoomListView.SelectedItem = freshRoom;
                break;
            }
        }

        if (!IsFirstJoinPasswordSetupMode(room))
        {
            isRoomPasswordPrepared = false;
            preparedRoomPassword = null;
            preparedRoomKey = null;
            SetStatus("Someone else already set the room password");
            UpdatePasswordInputGuidance();
            return;
        }

        try
        {
            ReserveRoomPasswordResultPayload result = await session.ReserveRoomPasswordAsync(
                room.Address,
                room.TcpPort,
                RoomPasswordBox.Password,
                CancellationToken.None);

            if (!result.Succeeded)
            {
                isRoomPasswordPrepared = false;
                preparedRoomPassword = null;
                preparedRoomKey = null;
                SetStatus(result.Message);
                UpdatePasswordInputGuidance();
                return;
            }

            preparedRoomPassword = RoomPasswordBox.Password;
            preparedRoomKey = BuildRoomKey(room);
            isRoomPasswordPrepared = true;
            SetStatus("Password saved \u2013 connect to activate it");
            UpdatePasswordInputGuidance();
            SaveRoomPasswordButton.Content = "Saved";
            await Task.Delay(1500);
            SaveRoomPasswordButton.Content = "Save Password";
        }
        catch (Exception exception)
        {
            isRoomPasswordPrepared = false;
            preparedRoomPassword = null;
            preparedRoomKey = null;
            SetStatus($"Save password failed: {exception.Message}");
            UpdatePasswordInputGuidance();
        }
    }

    private void RoomListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePasswordInputGuidance();
    }

    private void RoomPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (isRoomPasswordPrepared &&
            RoomListView.SelectedItem is DiscoveredRoom room &&
            string.Equals(preparedRoomKey, BuildRoomKey(room), StringComparison.Ordinal) &&
            !string.Equals(preparedRoomPassword, RoomPasswordBox.Password, StringComparison.Ordinal))
        {
            isRoomPasswordPrepared = false;
            preparedRoomPassword = null;
            preparedRoomKey = null;
        }

        UpdatePasswordInputGuidance();
    }

    private async void UserNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await SendUserRenameIfChangedAsync();
    }

    private async void UserNameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await SendUserRenameIfChangedAsync();
        }
    }

    private async Task SendUserRenameIfChangedAsync()
    {
        if (!session.IsConnected)
        {
            return;
        }

        string previousName = lastKnownUserName;
        string newName = GetSafeUserName();

        if (string.Equals(newName, previousName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await session.SendUserRenamedAsync(newName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetStatus($"Rename failed: {exception.Message}");
        }
    }
}