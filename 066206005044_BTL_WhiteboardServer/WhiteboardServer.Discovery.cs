using System.Net.Sockets;
using Btl.Whiteboard.Contracts;

internal sealed partial class WhiteboardServer
{
	private async Task RunDiscoveryLoopAsync(UdpClient discoverySocket, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			UdpReceiveResult received;
			try
			{
				received = await discoverySocket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (SocketException socketException)
			{
				Console.WriteLine($"[DISCOVERY] Receive error: {socketException.Message}");
				continue;
			}

			bool parsed = DiscoveryProtocol.TryParseDatagram<DiscoveryQueryPayload>(received.Buffer, out DiscoveryQueryPayload? query);
			if (!parsed || query is null || !string.Equals(query.AppId, DiscoveryProtocol.ApplicationId, StringComparison.Ordinal))
			{
				continue;
			}

			if (query.ProtocolVersion is not int queryProtocolVersion ||
				queryProtocolVersion != DiscoveryProtocol.CurrentProtocolVersion)
			{
				continue;
			}

			string? ownerUserName = null;
			bool isPasswordProtected;
			bool isFirstUserMode;
			lock (ownershipLock)
			{
				if (roomOwnerId is not null && clients.TryGetValue(roomOwnerId, out ConnectedClient? ownerClient))
				{
					ownerUserName = ownerClient.UserName;
				}

				CleanupExpiredPasswordReservationLocked(DateTimeOffset.UtcNow);
				isPasswordProtected = roomPasswordHash is not null;
				isFirstUserMode = !roomPasswordModeLocked && clients.IsEmpty && !HasActivePasswordReservationLocked(DateTimeOffset.UtcNow);
			}

			DiscoveryResponsePayload response = new(
				DiscoveryProtocol.ApplicationId,
				roomName,
				serverName,
				tcpPort,
				serverId,
				DiscoveryProtocol.CurrentProtocolVersion,
				isPasswordProtected,
				ownerUserName,
				isFirstUserMode);

			byte[] datagram = DiscoveryProtocol.ToDatagram(response);
			try
			{
				await discoverySocket.SendAsync(datagram, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
			}
			catch (SocketException socketException)
			{
				Console.WriteLine($"[DISCOVERY] Send error: {socketException.Message}");
			}
		}
	}

	private bool ValidateOrInitializePassword(string userId, string? password, out string failureMessage)
	{
		lock (ownershipLock)
		{
			failureMessage = "Invalid room password.";
			DateTimeOffset now = DateTimeOffset.UtcNow;
			CleanupExpiredPasswordReservationLocked(now);

			if (HasActivePasswordReservationLocked(now))
			{
				if (!string.Equals(reservedPasswordOwnerId, userId, StringComparison.Ordinal))
				{
					failureMessage = "Another client already reserved the room password.";
					return false;
				}

				if (string.IsNullOrWhiteSpace(password))
				{
					failureMessage = "Use the password you saved before connecting.";
					return false;
				}

				string reservedHash = ComputePasswordHash(password);
				if (!string.Equals(reservedHash, reservedPasswordHash, StringComparison.Ordinal))
				{
					failureMessage = "The saved room password does not match your current password.";
					return false;
				}

				roomPasswordHash = reservedPasswordHash;
				roomPasswordModeLocked = true;
				roomPasswordSetByFirstUser = true;
				ClearPasswordReservationLocked();
				Console.WriteLine("[AUTH] Reserved room password committed by reserving client.");
				return true;
			}

			if (!roomPasswordModeLocked)
			{
				if (!string.IsNullOrWhiteSpace(password))
				{
					roomPasswordHash = ComputePasswordHash(password);
					Console.WriteLine("[AUTH] Room password initialized by first connected user.");
				}

				roomPasswordModeLocked = true;
				roomPasswordSetByFirstUser = true;
				return true;
			}

			if (roomPasswordHash is null)
			{
				if (!string.IsNullOrWhiteSpace(password))
				{
					failureMessage = "Another client already opened this room without a password.";
					return false;
				}

				return true;
			}

			if (string.IsNullOrWhiteSpace(password))
			{
				failureMessage = "This room requires the password chosen by the first connected user.";
				return false;
			}

			string hash = ComputePasswordHash(password);
			if (string.Equals(hash, roomPasswordHash, StringComparison.Ordinal))
			{
				return true;
			}

			failureMessage = "This room password was already finalized by another client.";
			return false;
		}
	}

	private async Task ProcessReserveRoomPasswordRequestAsync(NetworkStream stream, WhiteboardEnvelope reserveEnvelope, CancellationToken cancellationToken)
	{
		ReserveRoomPasswordPayload payload;
		try
		{
			payload = reserveEnvelope.ReadPayload<ReserveRoomPasswordPayload>();
		}
		catch (InvalidDataException exception)
		{
			Console.WriteLine($"[AUTH] Invalid reserve-room-password payload: {exception.Message}");
			return;
		}

		ReserveRoomPasswordResultPayload result = TryReserveRoomPassword(payload.ClientId, payload.Password);
		WhiteboardEnvelope resultEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.ReserveRoomPasswordResult,
			"server",
			NextSequence(),
			result);

		await TrySendDirectEnvelopeAsync(stream, resultEnvelope, cancellationToken).ConfigureAwait(false);
	}

	private ReserveRoomPasswordResultPayload TryReserveRoomPassword(string clientId, string password)
	{
		string normalizedClientId = NormalizeClientUserId(clientId) ?? string.Empty;
		if (string.IsNullOrWhiteSpace(normalizedClientId))
		{
			return new ReserveRoomPasswordResultPayload(false, "Invalid client identifier.", null);
		}

		if (string.IsNullOrWhiteSpace(password))
		{
			return new ReserveRoomPasswordResultPayload(false, "Enter a password before saving.", null);
		}

		lock (ownershipLock)
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			CleanupExpiredPasswordReservationLocked(now);

			if (roomPasswordModeLocked || !clients.IsEmpty)
			{
				return new ReserveRoomPasswordResultPayload(false, "This room is no longer available for first-user password setup.", null);
			}

			string passwordHash = ComputePasswordHash(password);
			DateTimeOffset expiresAtUtc = now.Add(PasswordReservationTtl);

			if (HasActivePasswordReservationLocked(now))
			{
				if (!string.Equals(reservedPasswordOwnerId, normalizedClientId, StringComparison.Ordinal))
				{
					return new ReserveRoomPasswordResultPayload(false, "Another client already saved a password for this room.", reservedPasswordExpiresAtUtc?.ToUnixTimeMilliseconds());
				}

					reservedPasswordHash = passwordHash;
					reservedPasswordExpiresAtUtc = expiresAtUtc;
					Console.WriteLine($"[AUTH] Room password reservation refreshed by client {normalizedClientId[..6]}.");
					return new ReserveRoomPasswordResultPayload(true, "Room password reserved successfully.", expiresAtUtc.ToUnixTimeMilliseconds());
			}

			reservedPasswordOwnerId = normalizedClientId;
			reservedPasswordHash = passwordHash;
			reservedPasswordExpiresAtUtc = expiresAtUtc;
			Console.WriteLine($"[AUTH] Room password reserved by client {normalizedClientId[..6]} until {expiresAtUtc:HH:mm:ss} UTC.");
			return new ReserveRoomPasswordResultPayload(true, "Room password reserved successfully.", expiresAtUtc.ToUnixTimeMilliseconds());
		}
	}

	private bool HasActivePasswordReservationLocked(DateTimeOffset now)
	{
		return reservedPasswordOwnerId is not null &&
			reservedPasswordHash is not null &&
			reservedPasswordExpiresAtUtc is DateTimeOffset expiresAtUtc &&
			expiresAtUtc > now;
	}

	private void CleanupExpiredPasswordReservationLocked(DateTimeOffset now)
	{
		if (reservedPasswordExpiresAtUtc is not DateTimeOffset expiresAtUtc || expiresAtUtc > now)
		{
			return;
		}

		Console.WriteLine("[AUTH] Room password reservation expired.");
		ClearPasswordReservationLocked();
	}

	private void ClearPasswordReservationLocked()
	{
		reservedPasswordOwnerId = null;
		reservedPasswordHash = null;
		reservedPasswordExpiresAtUtc = null;
	}
}
