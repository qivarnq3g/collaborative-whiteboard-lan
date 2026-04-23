using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Btl.Whiteboard.Contracts;

internal sealed partial class WhiteboardServer
{
	private const int MaxUserNameLength = ProtocolConstants.MaxUserNameLength;
	private const int MaxUserIdLength = ProtocolConstants.MaxUserIdLength;
	private const int MaxChatLength = ProtocolConstants.MaxChatLength;
	private const int MaxStrokeIdLength = ProtocolConstants.MaxStrokeIdLength;
	private const int MaxLayerIdLength = ProtocolConstants.MaxLayerIdLength;
	private const int MaxColorHexLength = ProtocolConstants.MaxColorHexLength;
	private const double MinStrokeThickness = ProtocolConstants.MinStrokeThickness;
	private const double MaxStrokeThickness = ProtocolConstants.MaxStrokeThickness;
	private const int MaxHistoryCommands = 12_000;
	private const int TrimHistoryToCommands = 9_000;
	private static readonly TimeSpan ClientSendTimeout = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan PasswordReservationTtl = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan HistoryActionWindow = TimeSpan.FromSeconds(1);
	private static readonly int MaxHistoryActionsPerWindow = 12;

	private readonly string serverName;
	private readonly string roomName;
	private string? roomPasswordHash;
	private bool roomPasswordModeLocked;
	private bool roomPasswordSetByFirstUser;
	private readonly int tcpPort;
	private readonly int discoveryPort;
	private readonly string serverId = Guid.NewGuid().ToString("N");
	private readonly ConcurrentDictionary<string, ConnectedClient> clients = new();
	private readonly HashSet<string> bannedUserIds = new(StringComparer.Ordinal);
	private readonly List<StrokeCommandPayload> strokeHistory = new();
	private readonly Stack<List<StrokeCommandPayload>> redoHistory = new();
	private readonly Dictionary<string, string> activeStrokeOwners = new(StringComparer.Ordinal);
	private readonly Queue<DateTimeOffset> historyActionSamples = new();
	private readonly object historyLock = new();
	private readonly object ownershipLock = new();

	private string? roomOwnerId;
	private bool roomLocked;
	private string? roomLockedReason;
	private string? reservedPasswordOwnerId;
	private string? reservedPasswordHash;
	private DateTimeOffset? reservedPasswordExpiresAtUtc;
	private long sequence;

	public WhiteboardServer(string serverName, string roomName, string? roomPasswordHash, int tcpPort, int discoveryPort)
	{
		this.serverName = serverName;
		this.roomName = roomName;
		this.roomPasswordHash = roomPasswordHash;
		this.tcpPort = tcpPort;
		this.discoveryPort = discoveryPort;
		if (roomPasswordHash is not null)
		{
			roomPasswordModeLocked = true;
		}
	}

	public static string ComputePasswordHash(string password)
	{
		byte[] passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
		byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(passwordBytes);
		return System.Convert.ToHexString(hashBytes);
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

	private bool IsBannedUser(string userId)
	{
		lock (ownershipLock)
		{
			return bannedUserIds.Contains(userId);
		}
	}

	private bool IsRoomLocked(out string? reason)
	{
		lock (ownershipLock)
		{
			reason = roomLockedReason;
			return roomLocked;
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

	private static string? NormalizeClientUserId(string? userId)
	{
		string safe = (userId ?? string.Empty).Trim();
		if (safe.Length == 0 || safe.Length > ProtocolConstants.MaxUserIdLength)
		{
			return null;
		}

		foreach (char character in safe)
		{
			if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
			{
				continue;
			}

			return null;
		}

		return safe;
	}

	private static async Task TrySendDirectEnvelopeAsync(NetworkStream stream, WhiteboardEnvelope envelope, CancellationToken cancellationToken)
	{
		try
		{
			await FrameProtocol.WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
		}
	}
}
