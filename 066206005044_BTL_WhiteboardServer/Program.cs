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

	private readonly string serverName;
	private readonly string roomName;
	private readonly int tcpPort;
	private readonly int discoveryPort;
	private readonly string serverId = Guid.NewGuid().ToString("N");
	private readonly ConcurrentDictionary<string, ConnectedClient> clients = new();
	private readonly List<StrokeCommandPayload> strokeHistory = new();
	private readonly Stack<List<StrokeCommandPayload>> redoHistory = new();
	private readonly Dictionary<string, string> activeStrokeOwners = new(StringComparer.Ordinal);
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

		await BroadcastAsync(outgoingEnvelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
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
			!sender.TryConsumeStrokeSlot(DateTimeOffset.UtcNow))
		{
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

		await BroadcastAsync(outgoingEnvelope, exceptUserId: null, cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessUndoAsync(ConnectedClient sender, CancellationToken cancellationToken)
	{
		if (!sender.TryConsumeChatSlot(DateTimeOffset.UtcNow))
		{
			return;
		}

		if (HasActiveStrokeInProgress())
		{
			return;
		}

		if (!TryUndoLastStroke())
		{
			return;
		}

		await BroadcastCanvasSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ProcessRedoAsync(ConnectedClient sender, CancellationToken cancellationToken)
	{
		if (!sender.TryConsumeChatSlot(DateTimeOffset.UtcNow))
		{
			return;
		}

		if (HasActiveStrokeInProgress())
		{
			return;
		}

		if (!TryRedoLastStroke())
		{
			return;
		}

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

	private async Task BroadcastAsync(
		WhiteboardEnvelope envelope,
		string? exceptUserId,
		CancellationToken cancellationToken)
	{
		ConnectedClient[] recipients = clients.Values
			.Where(client => !string.Equals(client.UserId, exceptUserId, StringComparison.Ordinal))
			.ToArray();

		List<Task> tasks = new(recipients.Length);
		foreach (ConnectedClient recipient in recipients)
		{
			tasks.Add(SendToClientAsync(recipient, envelope, cancellationToken));
		}

		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			Console.WriteLine($"[TCP] Broadcast warning: {exception.Message}");
		}
	}

	private async Task SendToClientAsync(
		ConnectedClient client,
		WhiteboardEnvelope envelope,
		CancellationToken cancellationToken)
	{
		await client.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await FrameProtocol.WriteAsync(client.Stream, envelope, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			client.SendGate.Release();
		}
	}
}
