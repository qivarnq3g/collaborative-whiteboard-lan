using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Btl.Whiteboard.Contracts;

int tcpPort = ReadIntArg(args, "--tcp", DiscoveryProtocol.DefaultTcpPort);
int discoveryPort = ReadIntArg(args, "--discovery", DiscoveryProtocol.DefaultDiscoveryPort);
string roomName = ReadStringArg(args, "--room", DiscoveryProtocol.DefaultRoomName);

WhiteboardServer server = new(
	serverName: Environment.MachineName,
	roomName: roomName,
	tcpPort: tcpPort,
	discoveryPort: discoveryPort);

using CancellationTokenSource shutdownSource = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
	eventArgs.Cancel = true;
	shutdownSource.Cancel();
};

Console.WriteLine("========================================");
Console.WriteLine("BTL Collaborative Whiteboard Server");
Console.WriteLine($"Room: {roomName}");
Console.WriteLine($"TCP Port: {tcpPort}");
Console.WriteLine($"Discovery UDP Port: {discoveryPort}");
Console.WriteLine($"Protocol Version: {DiscoveryProtocol.CurrentProtocolVersion}");
PrintLanEndpoints(tcpPort, discoveryPort);
Console.WriteLine("Press Ctrl+C to stop server.");
Console.WriteLine("========================================");

await server.RunAsync(shutdownSource.Token).ConfigureAwait(false);

return;

static int ReadIntArg(string[] args, string key, int fallback)
{
	int keyIndex = Array.IndexOf(args, key);
	if (keyIndex < 0 || keyIndex + 1 >= args.Length)
	{
		return fallback;
	}

	if (int.TryParse(args[keyIndex + 1], out int value))
	{
		return value;
	}

	return fallback;
}

static string ReadStringArg(string[] args, string key, string fallback)
{
	int keyIndex = Array.IndexOf(args, key);
	if (keyIndex < 0 || keyIndex + 1 >= args.Length)
	{
		return fallback;
	}

	string value = args[keyIndex + 1].Trim();
	return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static void PrintLanEndpoints(int tcpPort, int discoveryPort)
{
	HashSet<string> addresses = new(StringComparer.Ordinal);

	foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
	{
		if (networkInterface.OperationalStatus != OperationalStatus.Up ||
			!networkInterface.Supports(NetworkInterfaceComponent.IPv4))
		{
			continue;
		}

		if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
			networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
		{
			continue;
		}

		IPInterfaceProperties interfaceProperties;
		try
		{
			interfaceProperties = networkInterface.GetIPProperties();
		}
		catch (NetworkInformationException)
		{
			continue;
		}

		foreach (UnicastIPAddressInformation unicastAddress in interfaceProperties.UnicastAddresses)
		{
			if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork ||
				IPAddress.IsLoopback(unicastAddress.Address))
			{
				continue;
			}

			byte[] bytes = unicastAddress.Address.GetAddressBytes();
			if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
			{
				continue;
			}

			addresses.Add(unicastAddress.Address.ToString());
		}
	}

	if (addresses.Count == 0)
	{
		Console.WriteLine("LAN endpoints: no active IPv4 adapter found.");
		return;
	}

	Console.WriteLine("LAN endpoints:");
	foreach (string address in addresses.OrderBy(value => value, StringComparer.Ordinal))
	{
		Console.WriteLine($"  - {address} (TCP {tcpPort}, UDP {discoveryPort})");
	}
}

internal sealed partial class WhiteboardServer
{
	private const int MaxUserNameLength = 36;
	private const int MaxChatLength = 400;
	private const int MaxStrokeIdLength = 80;
	private const int MaxLayerIdLength = 80;
	private const int MaxColorHexLength = 16;
	private const double MinStrokeThickness = 1;
	private const double MaxStrokeThickness = 24;
	private const int MaxHistoryCommands = 12_000;
	private const int TrimHistoryToCommands = 9_000;
	private static readonly TimeSpan ClientSendTimeout = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan HistoryActionWindow = TimeSpan.FromSeconds(1);
	private static readonly int MaxHistoryActionsPerWindow = 12;

	private readonly string serverName;
	private readonly string roomName;
	private readonly int tcpPort;
	private readonly int discoveryPort;
	private readonly string serverId = Guid.NewGuid().ToString("N");
	private readonly ConcurrentDictionary<string, ConnectedClient> clients = new();
	private readonly List<StrokeCommandPayload> strokeHistory = new();
	private readonly Stack<List<StrokeCommandPayload>> redoHistory = new();
	private readonly Dictionary<string, string> activeStrokeOwners = new(StringComparer.Ordinal);
	private readonly Queue<DateTimeOffset> historyActionSamples = new();
	private readonly object historyLock = new();

	private long sequence;

	public WhiteboardServer(string serverName, string roomName, int tcpPort, int discoveryPort)
	{
		this.serverName = serverName;
		this.roomName = roomName;
		this.tcpPort = tcpPort;
		this.discoveryPort = discoveryPort;
	}

	public async Task RunAsync(CancellationToken cancellationToken)
	{
		using TcpListener listener = new(IPAddress.Any, tcpPort);
		using UdpClient discoverySocket = new(discoveryPort);

		listener.Start();
		Console.WriteLine("Server is ready and accepting clients...");

		Task discoveryTask = RunDiscoveryLoopAsync(discoverySocket, cancellationToken);
		Task acceptTask = RunAcceptLoopAsync(listener, cancellationToken);

		try
		{
			await Task.WhenAll(discoveryTask, acceptTask).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			Console.WriteLine("Shutdown requested.");
		}
	}

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

			DiscoveryResponsePayload response = new(
				DiscoveryProtocol.ApplicationId,
				roomName,
				serverName,
				tcpPort,
				serverId,
				DiscoveryProtocol.CurrentProtocolVersion);

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
		string userId = Guid.NewGuid().ToString("N");
		ConnectedClient? registeredClient;

		try
		{
			registeredClient = await RegisterClientAsync(userId, tcpClient, cancellationToken).ConfigureAwait(false);
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

	private async Task<ConnectedClient?> RegisterClientAsync(
		string userId,
		TcpClient tcpClient,
		CancellationToken cancellationToken)
	{
		NetworkStream stream = tcpClient.GetStream();
		string remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";

		WhiteboardEnvelope? helloEnvelope;
		try
		{
			helloEnvelope = await FrameProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidDataException exception)
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: invalid hello frame. {exception.Message}");
			return null;
		}

		if (helloEnvelope is null || helloEnvelope.Type != WhiteboardMessageType.Hello)
		{
			Console.WriteLine($"[TCP] Rejected client {remoteEndpoint}: missing hello envelope.");
			return null;
		}

		HelloPayload helloPayload;
		try
		{
			helloPayload = helloEnvelope.ReadPayload<HelloPayload>();
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

		string userName = NormalizeUserName(helloPayload.UserName, $"Guest-{userId[..6]}");

		ConnectedClient connectedClient = new(userId, userName, tcpClient, stream);

		if (!clients.TryAdd(userId, connectedClient))
		{
			return null;
		}

		Console.WriteLine($"[TCP] Connected: {userName} ({userId[..6]})");

		await SendPresenceSnapshotAsync(connectedClient, cancellationToken).ConfigureAwait(false);
		await SendCanvasSnapshotAsync(connectedClient, cancellationToken).ConfigureAwait(false);

		WhiteboardEnvelope joinedEnvelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.UserJoined,
			"server",
			NextSequence(),
			new UserJoinedPayload(userId, userName));

		await BroadcastAsync(joinedEnvelope, exceptUserId: userId, cancellationToken).ConfigureAwait(false);
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

		await BroadcastPresenceSnapshotAsync(cancellationToken).ConfigureAwait(false);
		await connectedClient.DisposeAsync().ConfigureAwait(false);
	}

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

		if (text.Length > MaxChatLength)
		{
			text = text[..MaxChatLength];
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
			WhiteboardMessageType.StrokeStart => Convert(incomingEnvelope.ReadPayload<StrokeStartPayload>(), "start"),
			WhiteboardMessageType.StrokePoint => Convert(incomingEnvelope.ReadPayload<StrokePointPayload>(), "point"),
			WhiteboardMessageType.StrokeEnd => Convert(incomingEnvelope.ReadPayload<StrokeEndPayload>(), "end"),
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

		if (!TryUndoLastStroke())
		{
			Console.WriteLine($"[TCP] Undo ignored for {sender.UserName} ({sender.UserId[..6]}): no stroke history to undo.");
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

		if (!TryRedoLastStroke())
		{
			Console.WriteLine($"[TCP] Redo ignored for {sender.UserName} ({sender.UserId[..6]}): no redo history.");
			return;
		}

		Console.WriteLine($"[TCP] Redo applied for {sender.UserName} ({sender.UserId[..6]}).");

		await BroadcastCanvasSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task BroadcastPresenceSnapshotAsync(CancellationToken cancellationToken)
	{
		WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
			WhiteboardMessageType.PresenceSnapshot,
			"server",
			NextSequence(),
			BuildPresenceSnapshot());

		await BroadcastAsync(envelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
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

		return new PresenceSnapshotPayload(users);
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

	private bool TryConsumeHistoryActionSlot(DateTimeOffset now)
	{
		lock (historyLock)
		{
			DateTimeOffset lowerBound = now - HistoryActionWindow;
			while (historyActionSamples.Count > 0 && historyActionSamples.Peek() < lowerBound)
			{
				historyActionSamples.Dequeue();
			}

			if (historyActionSamples.Count >= MaxHistoryActionsPerWindow)
			{
				return false;
			}

			historyActionSamples.Enqueue(now);
			return true;
		}
	}
}
