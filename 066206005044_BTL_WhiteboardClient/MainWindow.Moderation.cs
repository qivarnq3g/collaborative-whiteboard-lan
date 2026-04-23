using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private void UpdateBannedUsersVisuals()
    {
        bool canUseBanControls = IsCurrentUserOwner() && session.IsConnected;
        if (BannedUsersComboBox is not null)
        {
            BannedUsersComboBox.IsEnabled = canUseBanControls;
            BannedUsersComboBox.PlaceholderText = bannedUsers.Count == 0
                ? "No banned users"
                : "Select user to unban";

            if (bannedUsers.Count == 0)
            {
                BannedUsersComboBox.SelectedItem = null;
            }
        }

        if (HostUnbanUserButton is not null)
        {
            HostUnbanUserButton.IsEnabled =
                canUseBanControls &&
                BannedUsersComboBox is not null &&
                BannedUsersComboBox.SelectedItem is BannedUserItem selectedItem &&
                !string.IsNullOrWhiteSpace(selectedItem.UserId);
        }
    }

    private void ApplyBanListSnapshot(IReadOnlyList<string> userIds)
    {
        if (!IsCurrentUserOwner())
        {
            bannedUsers.Clear();
            UpdateBannedUsersVisuals();
            return;
        }

        string[] normalizedUserIds = (userIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        string? previouslySelectedId = (BannedUsersComboBox.SelectedItem as BannedUserItem)?.UserId;

        bannedUsers.Clear();
        foreach (string userId in normalizedUserIds)
        {
            bannedUsers.Add(new BannedUserItem
            {
                UserId = userId,
                DisplayName = ResolveDisplayName(userId)
            });
        }

        BannedUserItem? toReselect = !string.IsNullOrWhiteSpace(previouslySelectedId)
            ? bannedUsers.FirstOrDefault(b => string.Equals(b.UserId, previouslySelectedId, StringComparison.Ordinal))
            : null;

        if (toReselect is not null)
        {
            BannedUsersComboBox.SelectedItem = toReselect;
        }
        else
        {
            BannedUsersComboBox.SelectedIndex = bannedUsers.Count > 0 ? 0 : -1;
        }

        UpdateBannedUsersVisuals();
    }

    private string ResolveDisplayName(string userId)
    {
        if (knownUserNames.TryGetValue(userId, out string? name) && !string.IsNullOrWhiteSpace(name))
        {
            return $"{name} ({userId[..Math.Min(8, userId.Length)]}...)";
        }

        return userId[..Math.Min(8, userId.Length)] + "...";
    }

    private void UpdateOnlineUserListItemActions()
    {
        string? selectedUserId = selectedTargetUserId;
        if (OnlineUsersListView.SelectedItem is OnlineUserItem selectedItem)
        {
            selectedUserId = selectedItem.UserId;
        }

        bool currentUserIsOwner = IsCurrentUserOwner();
        List<OnlineUserItem> rebuilt = onlineUsers
            .Select(user => new OnlineUserItem
            {
                UserId = user.UserId,
                UserName = user.UserName,
                IsOwner = string.Equals(user.UserId, roomOwnerId, StringComparison.Ordinal),
                CanModerate = currentUserIsOwner &&
                              !string.Equals(user.UserId, session.LocalSenderId, StringComparison.Ordinal) &&
                              !string.Equals(user.UserId, roomOwnerId, StringComparison.Ordinal)
            })
            .ToList();

        onlineUsers.Clear();
        foreach (OnlineUserItem rebuiltUser in rebuilt)
        {
            onlineUsers.Add(rebuiltUser);
        }

        selectedTargetUserId = null;
        if (!currentUserIsOwner)
        {
            OnlineUsersListView.SelectedItem = null;
            return;
        }

        if (selectedUserId is null)
        {
            return;
        }

        OnlineUserItem? restoredSelection = onlineUsers.FirstOrDefault(item =>
            string.Equals(item.UserId, selectedUserId, StringComparison.Ordinal) && item.CanModerate);

        OnlineUsersListView.SelectedItem = restoredSelection;
        selectedTargetUserId = restoredSelection?.UserId;
    }

    private void OnlineUsersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OnlineUsersListView.SelectedItem is OnlineUserItem selectedItem)
        {
            selectedTargetUserId = null;
            if (!IsCurrentUserOwner())
            {
                return;
            }

            if (string.Equals(selectedItem.UserId, session.LocalSenderId, StringComparison.Ordinal))
            {
                return;
            }

            selectedTargetUserId = selectedItem.UserId;
        }
    }

    private string? ResolveTargetUserIdFromSender(object sender)
    {
        FrameworkElement? sourceElement = sender as FrameworkElement;
        OnlineUserItem? item = sourceElement?.DataContext as OnlineUserItem;
        if (item is not null && item.CanModerate)
        {
            return item.UserId;
        }

        return selectedTargetUserId;
    }

    private async void KickUserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!session.IsConnected)
        {
            return;
        }

        if (!IsCurrentUserOwner())
        {
            SetStatus("Only the host can remove users");
            return;
        }

        string? targetUserId = ResolveTargetUserIdFromSender(sender);
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            SetStatus("Select a user first");
            return;
        }

        try
        {
            await session.SendKickUserAsync(targetUserId, "Kicked by host", CancellationToken.None).ConfigureAwait(false);
            SetStatus("User removed");
            OnlineUsersListView.SelectedItem = null;
            selectedTargetUserId = null;
        }
        catch (Exception exception)
        {
            SetStatus($"Kick failed: {exception.Message}");
        }
    }

    private async void BanUserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!session.IsConnected)
        {
            return;
        }

        if (!IsCurrentUserOwner())
        {
            SetStatus("Only the host can ban users");
            return;
        }

        string? targetUserId = ResolveTargetUserIdFromSender(sender);
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            SetStatus("Select a user first");
            return;
        }

        try
        {
            await session.SendBanUserAsync(targetUserId, "Banned by host", CancellationToken.None).ConfigureAwait(false);
            SetStatus("User banned");
            OnlineUsersListView.SelectedItem = null;
            selectedTargetUserId = null;
        }
        catch (Exception exception)
        {
            SetStatus($"Ban failed: {exception.Message}");
        }
    }

    private async void HostUnbanUserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!session.IsConnected)
        {
            return;
        }

        if (!IsCurrentUserOwner())
        {
            SetStatus("Only the host can unban users");
            return;
        }

        if (BannedUsersComboBox.SelectedItem is not BannedUserItem selectedBanned)
        {
            SetStatus("Select a banned user first.");
            return;
        }

        string targetUserId = selectedBanned.UserId;
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            SetStatus("Select a banned user first.");
            return;
        }

        try
        {
            await session.SendUnbanUserAsync(targetUserId, "Unbanned by host", CancellationToken.None).ConfigureAwait(false);
            SetStatus("User unbanned");
        }
        catch (Exception exception)
        {
            SetStatus($"Unban failed: {exception.Message}");
        }
    }

    private void BannedUsersComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBannedUsersVisuals();
    }

    private async void HostClearCanvasButton_Click(object sender, RoutedEventArgs e)
    {
        if (!session.IsConnected)
        {
            return;
        }

        if (!IsCurrentUserOwner())
        {
            SetStatus("Only the host can clear the canvas");
            return;
        }

        try
        {
            await session.SendClearCanvasAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus("Canvas cleared.");
        }
        catch (Exception exception)
        {
            SetStatus($"Clear canvas failed: {exception.Message}");
        }
    }

    private async void HostLockRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (!session.IsConnected)
        {
            return;
        }

        if (!IsCurrentUserOwner())
        {
            SetStatus("Only the host can change room access");
            return;
        }

        try
        {
            if (isRoomLocked)
            {
                await session.SendUnlockRoomAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus("Room is now open");
            }
            else
            {
                await session.SendLockRoomAsync("Room locked by host", CancellationToken.None).ConfigureAwait(false);
                SetStatus("Room locked \u2013 new members can't join");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Room access update failed: {exception.Message}");
        }
    }

    private void HostUnlockRoomButton_Click(object sender, RoutedEventArgs e)
    {
        HostLockRoomButton_Click(sender, e);
    }
}