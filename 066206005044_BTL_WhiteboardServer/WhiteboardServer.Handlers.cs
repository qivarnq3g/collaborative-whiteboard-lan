using Btl.Whiteboard.Contracts;

internal sealed partial class WhiteboardServer
{
	private async Task ProcessChatAsync(
		ConnectedClient sender,
		WhiteboardEnvelope incomingEnvelope,
		CancellationToken cancellationToken)
	{
		if (!sender.TryConsumeChatSlot(DateTimeOffset.UtcNow))
		{
			return;
		}

		ChatPayload payload = incomingEnvelope.ReadPayload<ChatPayload>();
		string text = payload.Text.Trim();
		if (text.Length == 0)
		{
			return;
		}

		if (text.Length > ProtocolConstants.MaxChatLength)
		{
			text = text[..ProtocolConstants.MaxChatLength];
		}

		WhiteboardEnvelope outgoingEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.Chat,
			sender.UserId,
			NextSequence(),
			new ChatPayload(sender.UserName, text));

		bool waitForCompletion = incomingEnvelope.Type != WhiteboardMessageType.StrokePoint;
		await BroadcastAsync(outgoingEnvelope, exceptUserId: null, cancellationToken, waitForCompletion).ConfigureAwait(false);
	}

	private async Task ProcessPingAsync(
		ConnectedClient sender,
		WhiteboardEnvelope incomingEnvelope,
		CancellationToken cancellationToken)
	{
		PingPayload payload = incomingEnvelope.ReadPayload<PingPayload>();

		WhiteboardEnvelope pongEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.Pong,
			"server",
			NextSequence(),
			new PongPayload(payload.SentUnixMilliseconds));

		await SendToClientAsync(sender, pongEnvelope, cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessStrokeAsync(
		ConnectedClient sender,
		WhiteboardEnvelope incomingEnvelope,
		CancellationToken cancellationToken)
	{
		if (incomingEnvelope.Type == WhiteboardMessageType.StrokePoint &&
			!sender.TryConsumeStrokeSlot(DateTimeOffset.UtcNow, out bool shouldReportThrottle))
		{
			if (shouldReportThrottle)
			{
				Console.WriteLine($"[TCP] Stroke throttle for {sender.UserName} ({sender.UserId[..6]}): stroke point rate exceeded.");
			}

			return;
		}

		StrokeCommandPayload command = incomingEnvelope.Type switch
		{
			WhiteboardMessageType.StrokeStart => WhiteboardServer.Convert(incomingEnvelope.ReadPayload<StrokeStartPayload>(), "start"),
			WhiteboardMessageType.StrokePoint => WhiteboardServer.Convert(incomingEnvelope.ReadPayload<StrokePointPayload>(), "point"),
			WhiteboardMessageType.StrokeEnd => WhiteboardServer.Convert(incomingEnvelope.ReadPayload<StrokeEndPayload>(), "end"),
			_ => throw new InvalidOperationException("Unsupported stroke message type.")
		};

		ValidateStrokeMessage(command.StrokeId, command.X, command.Y);

		if (!TryApplyStrokeCommand(sender, incomingEnvelope.Type, command, out StrokeCommandPayload normalizedCommand))
		{
			return;
		}

		WhiteboardEnvelope outgoingEnvelope = incomingEnvelope.Type switch
		{
			WhiteboardMessageType.StrokeStart => WhiteboardEnvelope.Create(
				WhiteboardMessageType.StrokeStart,
				sender.UserId,
				NextSequence(),
				new StrokeStartPayload(
					normalizedCommand.StrokeId,
					normalizedCommand.X,
					normalizedCommand.Y,
					sender.UserId,
					normalizedCommand.UserName,
					normalizedCommand.ColorHex,
					normalizedCommand.Thickness,
					normalizedCommand.LayerId)),

			WhiteboardMessageType.StrokePoint => WhiteboardEnvelope.Create(
				WhiteboardMessageType.StrokePoint,
				sender.UserId,
				NextSequence(),
				new StrokePointPayload(
					normalizedCommand.StrokeId,
					normalizedCommand.X,
					normalizedCommand.Y,
					sender.UserId,
					normalizedCommand.UserName,
					normalizedCommand.ColorHex,
					normalizedCommand.Thickness,
					normalizedCommand.LayerId)),

			_ => WhiteboardEnvelope.Create(
				WhiteboardMessageType.StrokeEnd,
				sender.UserId,
				NextSequence(),
				new StrokeEndPayload(
					normalizedCommand.StrokeId,
					normalizedCommand.X,
					normalizedCommand.Y,
					sender.UserId,
					normalizedCommand.UserName,
					normalizedCommand.ColorHex,
					normalizedCommand.Thickness,
					normalizedCommand.LayerId))
		};

		if (incomingEnvelope.Type == WhiteboardMessageType.StrokeEnd)
		{
			Console.WriteLine($"[TCP] StrokeEnd accepted from {sender.UserName} ({sender.UserId[..6]}), strokeId={normalizedCommand.StrokeId}.");
		}

		await BroadcastAsync(outgoingEnvelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessUndoAsync(ConnectedClient sender, CancellationToken cancellationToken)
	{
		Console.WriteLine($"[TCP] Undo request from {sender.UserName} ({sender.UserId[..6]}).");

		if (!TryConsumeHistoryActionSlot(DateTimeOffset.UtcNow))
		{
			Console.WriteLine($"[TCP] Undo throttled for {sender.UserName} ({sender.UserId[..6]}).");
			return;
		}

		if (HasActiveStrokeInProgress())
		{
			Console.WriteLine($"[TCP] Undo deferred for {sender.UserName} ({sender.UserId[..6]}): active stroke in progress.");
			return;
		}

		if (!TryUndoLastStrokeForUser(sender.UserId))
		{
			Console.WriteLine($"[TCP] Undo ignored for {sender.UserName} ({sender.UserId[..6]}): no own stroke history to undo.");
			return;
		}

		Console.WriteLine($"[TCP] Undo applied for {sender.UserName} ({sender.UserId[..6]}).");
		await BroadcastCanvasSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessRedoAsync(ConnectedClient sender, CancellationToken cancellationToken)
	{
		Console.WriteLine($"[TCP] Redo request from {sender.UserName} ({sender.UserId[..6]}).");

		if (!TryConsumeHistoryActionSlot(DateTimeOffset.UtcNow))
		{
			Console.WriteLine($"[TCP] Redo throttled for {sender.UserName} ({sender.UserId[..6]}).");
			return;
		}

		if (HasActiveStrokeInProgress())
		{
			Console.WriteLine($"[TCP] Redo deferred for {sender.UserName} ({sender.UserId[..6]}): active stroke in progress.");
			return;
		}

		if (!TryRedoLastStrokeForUser(sender.UserId))
		{
			Console.WriteLine($"[TCP] Redo ignored for {sender.UserName} ({sender.UserId[..6]}): no own redo history.");
			return;
		}

		Console.WriteLine($"[TCP] Redo applied for {sender.UserName} ({sender.UserId[..6]}).");
		await BroadcastCanvasSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessKickUserAsync(ConnectedClient sender, WhiteboardEnvelope incomingEnvelope, CancellationToken cancellationToken)
	{
		string? currentOwnerId;
		lock (ownershipLock)
		{
			currentOwnerId = roomOwnerId;
		}

		if (!string.Equals(sender.UserId, currentOwnerId, StringComparison.Ordinal))
		{
			Console.WriteLine($"[TCP] KickUser denied: {sender.UserName} ({sender.UserId[..6]}) is not the owner.");
			return;
		}

		KickPayload payload = incomingEnvelope.ReadPayload<KickPayload>();
		string targetId = payload.TargetUserId;
		string reason = (payload.Reason ?? string.Empty).Trim();

		if (string.Equals(targetId, sender.UserId, StringComparison.Ordinal))
		{
			return;
		}

		if (!clients.TryGetValue(targetId, out ConnectedClient? targetClient))
		{
			Console.WriteLine($"[TCP] KickUser: target {targetId[..6]} not found.");
			return;
		}

		Console.WriteLine($"[TCP] Kicking {targetClient.UserName} ({targetId[..6]}) by owner. Reason: {reason}");
		await SendToClientAsync(targetClient, WhiteboardEnvelope.Create(
			WhiteboardMessageType.KickUser,
			"server",
			NextSequence(),
			new KickPayload(targetId, reason)), cancellationToken).ConfigureAwait(false);
		_ = Task.Run(() => UnregisterClientAsync(targetClient, CancellationToken.None), CancellationToken.None);
	}

	private async Task ProcessBanUserAsync(ConnectedClient sender, WhiteboardEnvelope incomingEnvelope, CancellationToken cancellationToken)
	{
		string? currentOwnerId;
		lock (ownershipLock)
		{
			currentOwnerId = roomOwnerId;
		}

		if (!string.Equals(sender.UserId, currentOwnerId, StringComparison.Ordinal))
		{
			Console.WriteLine($"[TCP] BanUser denied: {sender.UserName} ({sender.UserId[..6]}) is not the owner.");
			return;
		}

		BanPayload payload = incomingEnvelope.ReadPayload<BanPayload>();
		string targetId = payload.TargetUserId;
		string reason = (payload.Reason ?? string.Empty).Trim();

		if (string.Equals(targetId, sender.UserId, StringComparison.Ordinal))
		{
			return;
		}

		bool isNewBan;
		lock (ownershipLock)
		{
			isNewBan = bannedUserIds.Add(targetId);
		}

		if (!isNewBan)
		{
			return;
		}

		Console.WriteLine($"[TCP] Banned {targetId[..6]} by owner. Reason: {reason}");

		if (clients.TryGetValue(targetId, out ConnectedClient? targetClient))
		{
			Console.WriteLine($"[TCP] Disconnecting banned client {targetClient.UserName} ({targetId[..6]}).");
			await SendToClientAsync(targetClient, WhiteboardEnvelope.Create(
				WhiteboardMessageType.BanUser,
				"server",
				NextSequence(),
				new BanPayload(targetId, reason)), cancellationToken).ConfigureAwait(false);
			_ = Task.Run(() => UnregisterClientAsync(targetClient, CancellationToken.None), CancellationToken.None);
		}

		await SendBanListSnapshotToOwnerAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessUnbanUserAsync(ConnectedClient sender, WhiteboardEnvelope incomingEnvelope, CancellationToken cancellationToken)
	{
		string? currentOwnerId;
		lock (ownershipLock)
		{
			currentOwnerId = roomOwnerId;
		}

		if (!string.Equals(sender.UserId, currentOwnerId, StringComparison.Ordinal))
		{
			Console.WriteLine($"[TCP] UnbanUser denied: {sender.UserName} ({sender.UserId[..6]}) is not the owner.");
			return;
		}

		UnbanPayload payload = incomingEnvelope.ReadPayload<UnbanPayload>();
		string targetId = payload.TargetUserId;
		if (string.IsNullOrWhiteSpace(targetId))
		{
			return;
		}

		bool removed;
		lock (ownershipLock)
		{
			removed = bannedUserIds.Remove(targetId);
		}

		if (!removed)
		{
			Console.WriteLine($"[TCP] UnbanUser ignored: target {targetId[..6]} not found in ban list.");
			return;
		}

		Console.WriteLine($"[TCP] Unbanned {targetId[..6]} by owner.");
		await SendBanListSnapshotToOwnerAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessClearCanvasAsync(ConnectedClient sender, CancellationToken cancellationToken)
	{
		string? currentOwnerId;
		lock (ownershipLock)
		{
			currentOwnerId = roomOwnerId;
		}

		if (!string.Equals(sender.UserId, currentOwnerId, StringComparison.Ordinal))
		{
			Console.WriteLine($"[TCP] ClearCanvas denied: {sender.UserName} ({sender.UserId[..6]}) is not the owner.");
			return;
		}

		lock (historyLock)
		{
			strokeHistory.Clear();
			redoHistory.Clear();
			activeStrokeOwners.Clear();
		}

		Console.WriteLine($"[TCP] Canvas cleared by owner {sender.UserName}.");
		await BroadcastCanvasSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessLockRoomAsync(ConnectedClient sender, WhiteboardEnvelope incomingEnvelope, CancellationToken cancellationToken)
	{
		string? currentOwnerId;
		lock (ownershipLock)
		{
			currentOwnerId = roomOwnerId;
		}

		if (!string.Equals(sender.UserId, currentOwnerId, StringComparison.Ordinal))
		{
			Console.WriteLine($"[TCP] LockRoom denied: {sender.UserName} ({sender.UserId[..6]}) is not the owner.");
			return;
		}

		LockRoomPayload payload = incomingEnvelope.ReadPayload<LockRoomPayload>();
		string reason = string.IsNullOrWhiteSpace(payload.Reason)
			? "Room is locked by owner."
			: payload.Reason.Trim();

		lock (ownershipLock)
		{
			roomLocked = true;
			roomLockedReason = reason;
		}

		Console.WriteLine($"[TCP] Room locked by owner {sender.UserName}. Reason: {reason}");
		WhiteboardEnvelope roomLockedEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.RoomLocked,
			"server",
			NextSequence(),
			new RoomLockedPayload(reason));
		await BroadcastAsync(roomLockedEnvelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessUnlockRoomAsync(ConnectedClient sender, CancellationToken cancellationToken)
	{
		string? currentOwnerId;
		lock (ownershipLock)
		{
			currentOwnerId = roomOwnerId;
		}

		if (!string.Equals(sender.UserId, currentOwnerId, StringComparison.Ordinal))
		{
			Console.WriteLine($"[TCP] UnlockRoom denied: {sender.UserName} ({sender.UserId[..6]}) is not the owner.");
			return;
		}

		lock (ownershipLock)
		{
			roomLocked = false;
			roomLockedReason = null;
		}

		Console.WriteLine($"[TCP] Room unlocked by owner {sender.UserName}.");
		WhiteboardEnvelope roomUnlockedEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.UnlockRoom,
			"server",
			NextSequence(),
			new UnlockRoomPayload());
		await BroadcastAsync(roomUnlockedEnvelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessRenameAsync(
		ConnectedClient sender,
		WhiteboardEnvelope incomingEnvelope,
		CancellationToken cancellationToken)
	{
		UserRenamedPayload payload = incomingEnvelope.ReadPayload<UserRenamedPayload>();
		string newName = payload.NewUserName?.Trim() ?? string.Empty;
		if (newName.Length == 0)
		{
			return;
		}

		if (newName.Length > MaxUserNameLength)
		{
			newName = newName[..MaxUserNameLength];
		}

		string oldName = sender.UserName;
		sender.UserName = newName;

		Console.WriteLine($"[TCP] User {oldName} ({sender.UserId[..6]}) renamed to {newName}.");

		WhiteboardEnvelope outgoing = WhiteboardEnvelope.Create(
			WhiteboardMessageType.UserRenamed,
			sender.UserId,
			NextSequence(),
			new UserRenamedPayload(sender.UserId, newName));
		await BroadcastAsync(outgoing, exceptUserId: null, cancellationToken).ConfigureAwait(false);
	}
}
