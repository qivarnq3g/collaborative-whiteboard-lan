using System.Linq;
using Btl.Whiteboard.Contracts;

internal sealed partial class WhiteboardServer
{
	private async Task BroadcastPresenceSnapshotAsync(CancellationToken cancellationToken)
	{
		WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.PresenceSnapshot,
			"server",
			NextSequence(),
			BuildPresenceSnapshot());

		await BroadcastAsync(envelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
	}

	private async Task SendBanListSnapshotToOwnerAsync(CancellationToken cancellationToken)
	{
		ConnectedClient? ownerClient = null;
		lock (ownershipLock)
		{
			if (roomOwnerId is not null)
			{
				clients.TryGetValue(roomOwnerId, out ownerClient);
			}
		}

		if (ownerClient is null)
		{
			return;
		}

		await SendBanListSnapshotAsync(ownerClient, cancellationToken).ConfigureAwait(false);
	}

	private async Task SendBanListSnapshotAsync(ConnectedClient client, CancellationToken cancellationToken)
	{
		string[] bannedUserIdsSnapshot;
		lock (ownershipLock)
		{
			bannedUserIdsSnapshot = bannedUserIds
				.OrderBy(value => value, StringComparer.Ordinal)
				.ToArray();
		}

		WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.BanListSnapshot,
			"server",
			NextSequence(),
			new BanListPayload(bannedUserIdsSnapshot));

		await SendToClientAsync(client, envelope, cancellationToken).ConfigureAwait(false);
	}

	private async Task SendPresenceSnapshotAsync(ConnectedClient client, CancellationToken cancellationToken)
	{
		WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.PresenceSnapshot,
			"server",
			NextSequence(),
			BuildPresenceSnapshot());

		await SendToClientAsync(client, envelope, cancellationToken).ConfigureAwait(false);
	}

	private async Task SendRoomStateInfoAsync(ConnectedClient client, CancellationToken cancellationToken)
	{
		WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.RoomStateInfo,
			"server",
			NextSequence(),
			BuildRoomStateInfoPayload());

		await SendToClientAsync(client, envelope, cancellationToken).ConfigureAwait(false);
	}

	private async Task SendCanvasSnapshotAsync(ConnectedClient client, CancellationToken cancellationToken)
	{
		WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.CanvasSnapshot,
			"server",
			NextSequence(),
			BuildCanvasSnapshotPayload());

		await SendToClientAsync(client, envelope, cancellationToken).ConfigureAwait(false);
	}

	private async Task BroadcastCanvasSnapshotAsync(CancellationToken cancellationToken)
	{
		WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.CanvasSnapshot,
			"server",
			NextSequence(),
			BuildCanvasSnapshotPayload());

		await BroadcastAsync(envelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
	}

	private CanvasSnapshotPayload BuildCanvasSnapshotPayload()
	{
		List<StrokeCommandPayload> commands;
		lock (historyLock)
		{
			commands = new List<StrokeCommandPayload>(strokeHistory);
		}

		return new CanvasSnapshotPayload(commands);
	}

	private PresenceSnapshotPayload BuildPresenceSnapshot()
	{
		List<PresenceUserPayload> users = clients.Values
			.OrderBy(client => client.UserName, StringComparer.OrdinalIgnoreCase)
			.Select(client => new PresenceUserPayload(client.UserId, client.UserName))
			.ToList();

		string? snapshotOwnerId;
		lock (ownershipLock)
		{
			snapshotOwnerId = roomOwnerId;
		}

		return new PresenceSnapshotPayload(users, snapshotOwnerId);
	}

	private RoomStateInfoPayload BuildRoomStateInfoPayload()
	{
		lock (ownershipLock)
		{
			CleanupExpiredPasswordReservationLocked(DateTimeOffset.UtcNow);
			string? ownerName = null;
			if (roomOwnerId is not null && clients.TryGetValue(roomOwnerId, out ConnectedClient? ownerClient))
			{
				ownerName = ownerClient.UserName;
			}

			bool isFirstUserMode = !roomPasswordModeLocked && clients.IsEmpty && !HasActivePasswordReservationLocked(DateTimeOffset.UtcNow);

			return new RoomStateInfoPayload(
				roomPasswordHash is not null,
				roomLocked,
				roomOwnerId,
				ownerName,
				isFirstUserMode);
		}
	}

	private Task BroadcastAsync(
		WhiteboardEnvelope envelope,
		string? exceptUserId,
		CancellationToken cancellationToken,
		bool waitForCompletion = true)
	{
		ConnectedClient[] recipients = clients.Values
			.Where(client => !string.Equals(client.UserId, exceptUserId, StringComparison.Ordinal))
			.ToArray();

		Task<ConnectedClient?>[] tasks = new Task<ConnectedClient?>[recipients.Length];
		for (int index = 0; index < recipients.Length; index++)
		{
			tasks[index] = SendToClientSafelyAsync(recipients[index], envelope, cancellationToken);
		}

		if (!waitForCompletion)
		{
			_ = HandleBroadcastFailuresAsync(tasks);
			return Task.CompletedTask;
		}

		return HandleBroadcastFailuresAsync(tasks);
	}

	private async Task HandleBroadcastFailuresAsync(IReadOnlyCollection<Task<ConnectedClient?>> tasks)
	{
		if (tasks.Count == 0)
		{
			return;
		}

		ConnectedClient?[] failedRecipients = await Task.WhenAll(tasks).ConfigureAwait(false);
		foreach (ConnectedClient? failedRecipient in failedRecipients)
		{
			if (failedRecipient is null)
			{
				continue;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await UnregisterClientAsync(failedRecipient, CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception exception)
				{
					Console.WriteLine($"[TCP] Cleanup warning for {failedRecipient.UserName}: {exception.Message}");
				}
			}, CancellationToken.None);
		}
	}

	private async Task<ConnectedClient?> SendToClientSafelyAsync(
		ConnectedClient client,
		WhiteboardEnvelope envelope,
		CancellationToken cancellationToken)
	{
		try
		{
			await SendToClientAsync(client, envelope, cancellationToken).ConfigureAwait(false);
			return null;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception exception)
		{
			Console.WriteLine($"[TCP] Send failure to {client.UserName} ({client.UserId[..6]}): {exception.Message}");
			return client;
		}
	}

	private async Task SendToClientAsync(
		ConnectedClient client,
		WhiteboardEnvelope envelope,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(ClientSendTimeout);

		await client.SendGate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
		try
		{
			await FrameProtocol.WriteAsync(client.Stream, envelope, timeoutSource.Token).ConfigureAwait(false);
		}
		finally
		{
			client.SendGate.Release();
		}
	}
}
