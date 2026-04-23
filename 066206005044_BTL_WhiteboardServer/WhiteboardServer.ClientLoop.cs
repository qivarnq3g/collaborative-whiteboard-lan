using System.Linq;
using System.Net;
using System.Net.Sockets;
using Btl.Whiteboard.Contracts;

internal sealed partial class WhiteboardServer
{
	private async Task RunAcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient tcpClient;
			try
			{
				tcpClient = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (SocketException socketException)
			{
				Console.WriteLine($"[TCP] Accept error: {socketException.Message}");
				continue;
			}

			_ = Task.Run(() => HandleClientAsync(tcpClient, cancellationToken), cancellationToken);
		}
	}

	private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
	{
		ConnectedClient? registeredClient;

		try
		{
			registeredClient = await RegisterClientAsync(tcpClient, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			string remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
			Console.WriteLine($"[TCP] Registration error from {remoteEndpoint}: {exception.GetType().Name}: {exception.Message}");
			tcpClient.Dispose();
			return;
		}

		if (registeredClient is null)
		{
			tcpClient.Dispose();
			return;
		}

		ConnectedClient connectedClient = registeredClient;
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				WhiteboardEnvelope? envelope = await FrameProtocol
					.ReadAsync(connectedClient.Stream, cancellationToken)
					.ConfigureAwait(false);

				if (envelope is null)
				{
					break;
				}

				switch (envelope.Type)
				{
					case WhiteboardMessageType.Ping:
						await ProcessPingAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.Pong:
						break;

					case WhiteboardMessageType.Chat:
						await ProcessChatAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.UndoRequest:
						await ProcessUndoAsync(connectedClient, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.RedoRequest:
						await ProcessRedoAsync(connectedClient, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.StrokeStart:
					case WhiteboardMessageType.StrokePoint:
					case WhiteboardMessageType.StrokeEnd:
						await ProcessStrokeAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.KickUser:
						await ProcessKickUserAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.BanUser:
						await ProcessBanUserAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.UnbanUser:
						await ProcessUnbanUserAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.LockRoom:
						await ProcessLockRoomAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.UnlockRoom:
						await ProcessUnlockRoomAsync(connectedClient, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.ClearCanvas:
						await ProcessClearCanvasAsync(connectedClient, cancellationToken).ConfigureAwait(false);
						break;

					case WhiteboardMessageType.UserRenamed:
						await ProcessRenameAsync(connectedClient, envelope, cancellationToken).ConfigureAwait(false);
						break;

					default:
						Console.WriteLine($"[TCP] Unsupported message type {(int)envelope.Type} from {connectedClient.UserName} ({connectedClient.UserId[..6]}).");
						break;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (InvalidDataException exception)
		{
			string remoteEndpoint = connectedClient.TcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
			Console.WriteLine($"[TCP] Protocol/frame error for {connectedClient.UserName} ({connectedClient.UserId[..6]}) from {remoteEndpoint}: {exception.Message}");
		}
		catch (Exception exception)
		{
			Console.WriteLine($"[TCP] Client loop error for {connectedClient.UserName}: {exception.GetType().Name}: {exception.Message}");
		}
		finally
		{
			await UnregisterClientAsync(connectedClient, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<ConnectedClient?> RegisterClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
	{
		NetworkStream stream = tcpClient.GetStream();
		string remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";

		WhiteboardEnvelope? firstEnvelope;
		try
		{
			firstEnvelope = await FrameProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidDataException exception)
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: invalid hello frame. {exception.Message}");
			return null;
		}

		if (firstEnvelope is null)
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: missing hello envelope.");
			return null;
		}

		if (firstEnvelope.Type == WhiteboardMessageType.ReserveRoomPassword)
		{
			await ProcessReserveRoomPasswordRequestAsync(stream, firstEnvelope, cancellationToken).ConfigureAwait(false);
			return null;
		}

		if (firstEnvelope.Type != WhiteboardMessageType.Hello)
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: unsupported initial envelope {firstEnvelope.Type}.");
			return null;
		}

		HelloPayload helloPayload;
		try
		{
			helloPayload = firstEnvelope.ReadPayload<HelloPayload>();
		}
		catch (InvalidDataException exception)
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: invalid hello payload. {exception.Message}");
			return null;
		}

		if (helloPayload.ProtocolVersion is not int protocolVersion ||
			protocolVersion != DiscoveryProtocol.CurrentProtocolVersion)
		{
			string clientProtocol = helloPayload.ProtocolVersion?.ToString() ?? "unknown";
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: protocol {clientProtocol}, expected {DiscoveryProtocol.CurrentProtocolVersion}.");
			return null;
		}

		if (!ValidateOrInitializePassword(helloPayload.UserId, helloPayload.Password, out string passwordFailureMessage))
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: {passwordFailureMessage}");
			WhiteboardEnvelope authFailedEnvelope = WhiteboardEnvelope.Create(
				WhiteboardMessageType.Error,
				"server",
				NextSequence(),
				new ErrorPayload("AUTH_FAILED", passwordFailureMessage));
			await TrySendDirectEnvelopeAsync(stream, authFailedEnvelope, cancellationToken).ConfigureAwait(false);
			return null;
		}

		string? normalizedUserId = NormalizeClientUserId(helloPayload.UserId);
		if (string.IsNullOrWhiteSpace(normalizedUserId))
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: invalid user identifier.");
			return null;
		}

		if (IsBannedUser(normalizedUserId))
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint} ({helloPayload.UserName}): banned.");
			WhiteboardEnvelope bannedEnvelope = WhiteboardEnvelope.Create(
				WhiteboardMessageType.Error,
				"server",
				NextSequence(),
				new ErrorPayload("BANNED", "You are banned from this room."));
			await TrySendDirectEnvelopeAsync(stream, bannedEnvelope, cancellationToken).ConfigureAwait(false);
			return null;
		}

		if (IsRoomLocked(out string? lockReason))
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint} ({helloPayload.UserName}): room is locked.");
			WhiteboardEnvelope lockEnvelope = WhiteboardEnvelope.Create(
				WhiteboardMessageType.RoomLocked,
				"server",
				NextSequence(),
				new RoomLockedPayload(lockReason ?? "Room is locked by owner."));
			await TrySendDirectEnvelopeAsync(stream, lockEnvelope, cancellationToken).ConfigureAwait(false);
			return null;
		}

		string userName = NormalizeUserName(helloPayload.UserName, $"Guest-{normalizedUserId[..6]}");

		ConnectedClient connectedClient = new(normalizedUserId, userName, tcpClient, stream);

		if (!clients.TryAdd(normalizedUserId, connectedClient))
		{
			return null;
		}

		bool isFirstClient;
		lock (ownershipLock)
		{
			isFirstClient = roomOwnerId is null;
			if (isFirstClient)
			{
				roomOwnerId = normalizedUserId;
			}
		}

		if (isFirstClient)
		{
			Console.WriteLine($"[TCP] Ownership assigned: {userName} ({normalizedUserId[..6]}) is the room owner.");
		}

		Console.WriteLine($"[TCP] Connected: {userName} ({normalizedUserId[..6]})");

		await SendRoomStateInfoAsync(connectedClient, cancellationToken).ConfigureAwait(false);
		await SendPresenceSnapshotAsync(connectedClient, cancellationToken).ConfigureAwait(false);
		await SendCanvasSnapshotAsync(connectedClient, cancellationToken).ConfigureAwait(false);
		if (string.Equals(roomOwnerId, normalizedUserId, StringComparison.Ordinal))
		{
			await SendBanListSnapshotAsync(connectedClient, cancellationToken).ConfigureAwait(false);
		}

		WhiteboardEnvelope joinedEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.UserJoined,
			"server",
			NextSequence(),
			new UserJoinedPayload(normalizedUserId, userName));

		await BroadcastAsync(joinedEnvelope, exceptUserId: normalizedUserId, cancellationToken).ConfigureAwait(false);
		await BroadcastPresenceSnapshotAsync(cancellationToken).ConfigureAwait(false);

		return connectedClient;
	}

	private async Task UnregisterClientAsync(ConnectedClient connectedClient, CancellationToken cancellationToken)
	{
		if (!clients.TryRemove(connectedClient.UserId, out _))
		{
			return;
		}

		Console.WriteLine($"[TCP] Disconnected: {connectedClient.UserName} ({connectedClient.UserId[..6]})");
		bool removedDanglingStroke = RemoveActiveStrokesForUser(connectedClient.UserId);

		string? newOwnerId = null;
		string? newOwnerName = null;
		bool ownerWasHere = false;
		lock (ownershipLock)
		{
			if (string.Equals(roomOwnerId, connectedClient.UserId, StringComparison.Ordinal))
			{
				ownerWasHere = true;
				roomOwnerId = null;
				if (clients.Count > 0)
				{
					ConnectedClient? oldestClient = clients.Values
						.OrderBy(c => c.UserId, StringComparer.Ordinal)
						.FirstOrDefault();
					if (oldestClient is not null)
					{
						roomOwnerId = oldestClient.UserId;
						newOwnerId = oldestClient.UserId;
						newOwnerName = oldestClient.UserName;
					}
				}
				else
				{
					roomLocked = false;
					roomLockedReason = null;
					if (roomPasswordSetByFirstUser)
					{
						roomPasswordHash = null;
						roomPasswordModeLocked = false;
						roomPasswordSetByFirstUser = false;
						ClearPasswordReservationLocked();
						Console.WriteLine("[AUTH] Room is now empty. Password reset for next session.");
					}
				}
			}
		}

		WhiteboardEnvelope leftEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.UserLeft,
			"server",
			NextSequence(),
			new UserLeftPayload(connectedClient.UserId, connectedClient.UserName));

		await BroadcastAsync(leftEnvelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
		if (removedDanglingStroke)
		{
			await BroadcastCanvasSnapshotAsync(cancellationToken).ConfigureAwait(false);
		}

		if (ownerWasHere && newOwnerId is not null && newOwnerName is not null)
		{
			Console.WriteLine($"[TCP] Ownership transferred to {newOwnerName} ({newOwnerId[..6]}).");
			WhiteboardEnvelope ownerEnvelope = WhiteboardEnvelope.Create(
				WhiteboardMessageType.OwnerAssigned,
				"server",
				NextSequence(),
				new OwnerAssignedPayload(newOwnerId, newOwnerName));
			await BroadcastAsync(ownerEnvelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);

			if (clients.TryGetValue(newOwnerId, out ConnectedClient? newOwnerClient))
			{
				await SendBanListSnapshotAsync(newOwnerClient, cancellationToken).ConfigureAwait(false);
			}
		}

		await BroadcastPresenceSnapshotAsync(cancellationToken).ConfigureAwait(false);
		await connectedClient.DisposeAsync().ConfigureAwait(false);
	}
}
