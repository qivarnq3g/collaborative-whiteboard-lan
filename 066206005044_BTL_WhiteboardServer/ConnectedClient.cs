using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

internal sealed class ConnectedClient : IAsyncDisposable
{
	private static readonly TimeSpan ChatWindow = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan StrokeWindow = TimeSpan.FromSeconds(1);
	private const int MaxChatMessagesPerWindow = 6;
	private const int MaxStrokeMessagesPerWindow = 600;

	private readonly Queue<DateTimeOffset> chatSamples = new();
	private readonly Queue<DateTimeOffset> strokeSamples = new();
	private readonly object rateGate = new();

	public ConnectedClient(string userId, string userName, TcpClient tcpClient, NetworkStream stream)
	{
		UserId = userId;
		UserName = userName;
		TcpClient = tcpClient;
		Stream = stream;
	}

	public string UserId { get; }

	public string UserName { get; }

	public TcpClient TcpClient { get; }

	public NetworkStream Stream { get; }

	public SemaphoreSlim SendGate { get; } = new(1, 1);

	public bool TryConsumeChatSlot(DateTimeOffset now)
	{
		lock (rateGate)
		{
			return TryConsume(chatSamples, now, ChatWindow, MaxChatMessagesPerWindow);
		}
	}

	public bool TryConsumeStrokeSlot(DateTimeOffset now)
	{
		lock (rateGate)
		{
			return TryConsume(strokeSamples, now, StrokeWindow, MaxStrokeMessagesPerWindow);
		}
	}

	public ValueTask DisposeAsync()
	{
		SendGate.Dispose();
		Stream.Dispose();
		TcpClient.Dispose();
		return ValueTask.CompletedTask;
	}

	private static bool TryConsume(
		Queue<DateTimeOffset> samples,
		DateTimeOffset now,
		TimeSpan window,
		int maxItems)
	{
		DateTimeOffset lowerBound = now - window;
		while (samples.Count > 0 && samples.Peek() < lowerBound)
		{
			samples.Dequeue();
		}

		if (samples.Count >= maxItems)
		{
			return false;
		}

		samples.Enqueue(now);
		return true;
	}
}
