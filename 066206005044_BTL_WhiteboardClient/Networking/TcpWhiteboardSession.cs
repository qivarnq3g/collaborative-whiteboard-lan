using System.Net.Sockets;
using Btl.Whiteboard.Contracts;

namespace Btl.Whiteboard.Client.Networking;

public sealed class TcpWhiteboardSession : IAsyncDisposable
{
    private const int MaxUserNameLength = 36;
    private const int MaxChatLength = 400;
    private const int MaxStrokeIdLength = 80;
    private const int MaxLayerIdLength = 80;
    private const int MaxColorHexLength = 16;
    private const double MinStrokeThickness = 1;
    private const double MaxStrokeThickness = 24;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReconnectBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(8);

    private readonly string localSenderId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim reconnectGate = new(1, 1);

    private TcpClient? tcpClient;
    private NetworkStream? stream;
    private CancellationTokenSource? lifecycleSource;
    private CancellationTokenSource? receiveLoopSource;
    private Task? receiveLoopTask;
    private Task? heartbeatTask;
    private long outgoingSequence;
    private DateTimeOffset lastReceiveAtUtc;
    private bool isManualDisconnect;
    private int reconnectAttempt;
    private string configuredHost = string.Empty;
    private int configuredPort;
    private string configuredUserName = "Guest";

    public event EventHandler<WhiteboardEnvelope>? EnvelopeReceived;

    public event EventHandler<string>? ConnectionStateChanged;

    public bool IsConnected => tcpClient is { Connected: true } && stream is not null;

    public async Task ConnectAsync(string host, int port, string userName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Host is required.");
        }

        await reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            configuredHost = host.Trim();
            configuredPort = port;
            configuredUserName = NormalizeUserName(userName);
            isManualDisconnect = false;
            reconnectAttempt = 0;

            lifecycleSource?.Cancel();
            lifecycleSource?.Dispose();
            lifecycleSource = new CancellationTokenSource();

            ConnectionStateChanged?.Invoke(this, "Connecting");

            await OpenConnectionAsync(
                configuredHost,
                configuredPort,
                configuredUserName,
                cancellationToken,
                lifecycleSource.Token).ConfigureAwait(false);

            StartHeartbeatLoop();
            ConnectionStateChanged?.Invoke(this, "Connected");
        }
        finally
        {
            reconnectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        isManualDisconnect = true;

        lifecycleSource?.Cancel();
        receiveLoopSource?.Cancel();

        CloseSocket();

        if (receiveLoopTask is not null)
        {
            try
            {
                await receiveLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (heartbeatTask is not null)
        {
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        CleanupConnection();
        lifecycleSource?.Dispose();
        lifecycleSource = null;

        ConnectionStateChanged?.Invoke(this, "Disconnected");
    }

    public Task SendChatAsync(string userName, string text, CancellationToken cancellationToken)
    {
        string safeUserName = NormalizeUserName(userName);
        string safeText = NormalizeChatText(text);

        WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
            WhiteboardMessageType.Chat,
            localSenderId,
            NextSequence(),
            new ChatPayload(safeUserName, safeText));

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendStrokeStartAsync(
        string strokeId,
        double x,
        double y,
        string userName,
        string colorHex,
        double thickness,
        string layerId,
        CancellationToken cancellationToken)
    {
        ValidateStrokeMessage(strokeId, x, y);
        string safeColorHex = NormalizeColorHex(colorHex);
        double safeThickness = NormalizeStrokeThickness(thickness);
        string safeLayerId = NormalizeLayerId(layerId);

        WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
            WhiteboardMessageType.StrokeStart,
            localSenderId,
            NextSequence(),
            new StrokeStartPayload(strokeId, x, y, NormalizeUserName(userName), safeColorHex, safeThickness, safeLayerId));

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendStrokePointAsync(
        string strokeId,
        double x,
        double y,
        string userName,
        string colorHex,
        double thickness,
        string layerId,
        CancellationToken cancellationToken)
    {
        ValidateStrokeMessage(strokeId, x, y);
        string safeColorHex = NormalizeColorHex(colorHex);
        double safeThickness = NormalizeStrokeThickness(thickness);
        string safeLayerId = NormalizeLayerId(layerId);

        WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
            WhiteboardMessageType.StrokePoint,
            localSenderId,
            NextSequence(),
            new StrokePointPayload(strokeId, x, y, NormalizeUserName(userName), safeColorHex, safeThickness, safeLayerId));

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendStrokeEndAsync(
        string strokeId,
        double x,
        double y,
        string userName,
        string colorHex,
        double thickness,
        string layerId,
        CancellationToken cancellationToken)
    {
        ValidateStrokeMessage(strokeId, x, y);
        string safeColorHex = NormalizeColorHex(colorHex);
        double safeThickness = NormalizeStrokeThickness(thickness);
        string safeLayerId = NormalizeLayerId(layerId);

        WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
            WhiteboardMessageType.StrokeEnd,
            localSenderId,
            NextSequence(),
            new StrokeEndPayload(strokeId, x, y, NormalizeUserName(userName), safeColorHex, safeThickness, safeLayerId));

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendUndoAsync(string userName, CancellationToken cancellationToken)
    {
        WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
            WhiteboardMessageType.UndoRequest,
            localSenderId,
            NextSequence(),
            new UndoPayload(NormalizeUserName(userName)));

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendRedoAsync(string userName, CancellationToken cancellationToken)
    {
        WhiteboardEnvelope envelope = WhiteboardEnvelope.Create(
            WhiteboardMessageType.RedoRequest,
            localSenderId,
            NextSequence(),
            new RedoPayload(NormalizeUserName(userName)));

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        sendGate.Dispose();
        reconnectGate.Dispose();
    }

    private async Task OpenConnectionAsync(
        string host,
        int port,
        string userName,
        CancellationToken externalToken,
        CancellationToken lifecycleToken)
    {
        using CancellationTokenSource connectSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken, lifecycleToken);

        TcpClient newClient = new();
        newClient.NoDelay = true;
        await newClient.ConnectAsync(host, port, connectSource.Token).ConfigureAwait(false);

        NetworkStream newStream = newClient.GetStream();

        receiveLoopSource?.Cancel();
        receiveLoopSource?.Dispose();
        receiveLoopSource = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken);

        tcpClient = newClient;
        stream = newStream;
        lastReceiveAtUtc = DateTimeOffset.UtcNow;

        receiveLoopTask = Task.Run(() => RunReceiveLoopAsync(receiveLoopSource.Token), CancellationToken.None);

        WhiteboardEnvelope helloEnvelope = WhiteboardEnvelope.Create(
            WhiteboardMessageType.Hello,
            localSenderId,
            NextSequence(),
            new HelloPayload(userName, DiscoveryProtocol.CurrentProtocolVersion));

        await SendEnvelopeAsync(helloEnvelope, connectSource.Token).ConfigureAwait(false);
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        bool shouldReconnect = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (stream is null)
                {
                    break;
                }

                WhiteboardEnvelope? envelope = await FrameProtocol
                    .ReadAsync(stream, cancellationToken)
                    .ConfigureAwait(false);

                if (envelope is null)
                {
                    break;
                }

                lastReceiveAtUtc = DateTimeOffset.UtcNow;

                if (envelope.Type == WhiteboardMessageType.Ping)
                {
                    PingPayload pingPayload = envelope.ReadPayload<PingPayload>();
                    WhiteboardEnvelope pongEnvelope = WhiteboardEnvelope.Create(
                        WhiteboardMessageType.Pong,
                        localSenderId,
                        NextSequence(),
                        new PongPayload(pingPayload.SentUnixMilliseconds));

                    await SendEnvelopeAsync(pongEnvelope, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == WhiteboardMessageType.Pong)
                {
                    continue;
                }

                EnvelopeReceived?.Invoke(this, envelope);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidDataException exception)
        {
            ConnectionStateChanged?.Invoke(this, $"Connection error: protocol/frame mismatch. {exception.Message}");
        }
        catch (Exception exception)
        {
            ConnectionStateChanged?.Invoke(this, $"Connection error: {exception.Message}");
        }
        finally
        {
            shouldReconnect = !isManualDisconnect && lifecycleSource is { IsCancellationRequested: false };
            CleanupConnection();
        }

        if (shouldReconnect && lifecycleSource is not null)
        {
            await ReconnectLoopAsync(lifecycleSource.Token).ConfigureAwait(false);
        }
    }

    private void StartHeartbeatLoop()
    {
        if (lifecycleSource is null)
        {
            return;
        }

        if (heartbeatTask is { IsCompleted: false })
        {
            return;
        }

        heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(lifecycleSource.Token), CancellationToken.None);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!IsConnected)
            {
                continue;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - lastReceiveAtUtc > HeartbeatTimeout)
            {
                ConnectionStateChanged?.Invoke(this, "Connection error: heartbeat timeout");
                ForceConnectionReset();
                continue;
            }

            WhiteboardEnvelope pingEnvelope = WhiteboardEnvelope.Create(
                WhiteboardMessageType.Ping,
                localSenderId,
                NextSequence(),
                new PingPayload(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            try
            {
                await SendEnvelopeAsync(pingEnvelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ConnectionStateChanged?.Invoke(this, $"Connection error: {exception.Message}");
                ForceConnectionReset();
            }
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredHost))
        {
            ConnectionStateChanged?.Invoke(this, "Disconnected");
            return;
        }

        await reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected || isManualDisconnect)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested && !isManualDisconnect)
            {
                reconnectAttempt++;
                ConnectionStateChanged?.Invoke(this, $"Reconnecting ({reconnectAttempt})");

                TimeSpan delay = ComputeReconnectDelay(reconnectAttempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                try
                {
                    await OpenConnectionAsync(
                        configuredHost,
                        configuredPort,
                        configuredUserName,
                        cancellationToken,
                        cancellationToken).ConfigureAwait(false);

                    reconnectAttempt = 0;
                    StartHeartbeatLoop();
                    ConnectionStateChanged?.Invoke(this, "Connected");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    ConnectionStateChanged?.Invoke(this, $"Connection error: {exception.Message}");
                }
            }
        }
        finally
        {
            reconnectGate.Release();
        }

        if (!IsConnected)
        {
            ConnectionStateChanged?.Invoke(this, "Disconnected");
        }
    }

    private async Task SendEnvelopeAsync(WhiteboardEnvelope envelope, CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkStream activeStream = GetWritableStream();
            await FrameProtocol.WriteAsync(activeStream, envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException exception)
        {
            throw HandleSendFailure("connection stream is already disposed", exception);
        }
        catch (IOException exception)
        {
            throw HandleSendFailure("network stream write failed", exception);
        }
        catch (SocketException exception)
        {
            throw HandleSendFailure("socket send failed", exception);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private NetworkStream GetWritableStream()
    {
        NetworkStream? activeStream = stream;
        TcpClient? activeClient = tcpClient;

        if (activeStream is null || activeClient is null || !activeClient.Connected || !activeStream.CanWrite)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        return activeStream;
    }

    private InvalidOperationException HandleSendFailure(string reason, Exception exception)
    {
        ConnectionStateChanged?.Invoke(this, $"Connection error: {reason}. {exception.Message}");
        ForceConnectionReset();
        return new InvalidOperationException($"Unable to send data because {reason}.", exception);
    }

    private void ForceConnectionReset()
    {
        receiveLoopSource?.Cancel();
        CloseSocket();
    }

    private void CloseSocket()
    {
        if (tcpClient is null)
        {
            return;
        }

        try
        {
            tcpClient.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        tcpClient.Close();
    }

    private long NextSequence()
    {
        return Interlocked.Increment(ref outgoingSequence);
    }

    private void CleanupConnection()
    {
        stream?.Dispose();
        stream = null;

        tcpClient?.Dispose();
        tcpClient = null;

        receiveLoopSource?.Dispose();
        receiveLoopSource = null;

        receiveLoopTask = null;
    }

    private static string NormalizeUserName(string userName)
    {
        string safe = userName.Trim();
        if (safe.Length == 0)
        {
            safe = "Guest";
        }

        if (safe.Length > MaxUserNameLength)
        {
            safe = safe[..MaxUserNameLength];
        }

        return safe;
    }

    private static string NormalizeChatText(string text)
    {
        string safe = text.Trim();
        if (safe.Length == 0)
        {
            throw new InvalidOperationException("Chat text cannot be empty.");
        }

        if (safe.Length > MaxChatLength)
        {
            safe = safe[..MaxChatLength];
        }

        return safe;
    }

    private static void ValidateStrokeMessage(string strokeId, double x, double y)
    {
        if (string.IsNullOrWhiteSpace(strokeId) || strokeId.Length > MaxStrokeIdLength)
        {
            throw new InvalidOperationException("Stroke identifier is invalid.");
        }

        if (!IsFinite(x) || !IsFinite(y))
        {
            throw new InvalidOperationException("Stroke coordinates must be finite values.");
        }
    }

    private static bool IsFinite(double value)
    {
        return !(double.IsNaN(value) || double.IsInfinity(value));
    }

    private static string NormalizeColorHex(string colorHex)
    {
        string safe = (colorHex ?? string.Empty).Trim();
        if (safe.Length == 0 || safe.Length > MaxColorHexLength)
        {
            return "#1F6FEB";
        }

        if (safe[0] != '#')
        {
            safe = $"#{safe}";
        }

        return safe.ToUpperInvariant();
    }

    private static double NormalizeStrokeThickness(double thickness)
    {
        if (!IsFinite(thickness))
        {
            return 2.4;
        }

        if (thickness < MinStrokeThickness)
        {
            return MinStrokeThickness;
        }

        if (thickness > MaxStrokeThickness)
        {
            return MaxStrokeThickness;
        }

        return thickness;
    }

    private static string NormalizeLayerId(string layerId)
    {
        string safe = (layerId ?? string.Empty).Trim();
        if (safe.Length == 0)
        {
            return "layer-1";
        }

        if (safe.Length > MaxLayerIdLength)
        {
            safe = safe[..MaxLayerIdLength];
        }

        return safe;
    }

    private static TimeSpan ComputeReconnectDelay(int attempt)
    {
        double multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        double delayMilliseconds = ReconnectBaseDelay.TotalMilliseconds * multiplier;
        if (delayMilliseconds > ReconnectMaxDelay.TotalMilliseconds)
        {
            delayMilliseconds = ReconnectMaxDelay.TotalMilliseconds;
        }

        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }
}